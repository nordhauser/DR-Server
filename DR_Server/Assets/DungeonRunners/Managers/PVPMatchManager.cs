using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DungeonRunners.Managers
{
    /// <summary>
    /// Manages PVP match queues and lifecycle for non-duel PVP modes.
    ///
    /// Handles GroupClient channel 0x0B opcodes (binary-verified):
    ///   0x29 = enterPVPZone       — player wants to enter the PVP hub area
    ///   0x2A = requestPVPMatch    — payload: match archetype gctype string
    ///   0x2B = cancelPVPMatch     — leave queue
    ///   0x2C = leavePVP           — leave entire PVP system (return to town)
    ///
    /// Match archetypes (from /Database/gc/):
    ///   - GroupDeathMatch          : ranked team-vs-team DM
    ///   - GroupDeathMatchUnrated   : casual team DM
    ///   - GroupDuelMatch           : ranked 1v1 (separate from /duel command)
    ///   - GroupPracticeMatch       : casual 1v1
    ///
    /// Match zones (from /World/, .world files extending base.World_cat_pvp):
    ///   - PVPGroupDuelMatch        : 1v1 arenas
    ///   - DeathMatch01..04         : 4-player FFA
    ///   - PVPGroupDeathMatch       : team DM
    ///
    /// Lifecycle:
    ///   queued → matched → ready_check → spawning → in_progress → results → ended
    ///
    /// All state is in-memory. Persisted PVP records (wins/losses/rating) live in the
    /// players table and are updated via QuestManager.UpdatePvpRecord on match end.
    /// </summary>
    public class PVPMatchManager
    {
        public static readonly PVPMatchManager Instance = new PVPMatchManager();

        // ── Match archetype definitions ─────────────────────────────────────
        public enum Archetype
        {
            GroupDuelMatch,         // ranked 1v1
            GroupPracticeMatch,     // casual 1v1
            GroupDeathMatch,        // ranked team DM (4 players)
            GroupDeathMatchUnrated, // casual DM (4 players)
        }

        private static readonly Dictionary<string, Archetype> _archetypeByGcType =
            new Dictionary<string, Archetype>(StringComparer.OrdinalIgnoreCase)
            {
                { "world.matches.GroupDuelMatch",         Archetype.GroupDuelMatch },
                { "world.matches.GroupPracticeMatch",     Archetype.GroupPracticeMatch },
                { "world.matches.GroupDeathMatch",        Archetype.GroupDeathMatch },
                { "world.matches.GroupDeathMatchUnrated", Archetype.GroupDeathMatchUnrated },
            };

        public static bool TryParseArchetype(string gcType, out Archetype a)
            => _archetypeByGcType.TryGetValue(gcType ?? "", out a);

        public static int RequiredPlayers(Archetype a)
        {
            switch (a)
            {
                case Archetype.GroupDuelMatch:
                case Archetype.GroupPracticeMatch:
                    return 2;
                case Archetype.GroupDeathMatch:
                case Archetype.GroupDeathMatchUnrated:
                    return 4;
                default: return 2;
            }
        }

        public static bool IsRanked(Archetype a)
        {
            switch (a)
            {
                case Archetype.GroupDuelMatch:
                case Archetype.GroupDeathMatch:
                    return true;
                default: return false;
            }
        }

        /// <summary>Pick a match-zone for an archetype. Round-robin within the available zones.</summary>
        public string PickZoneForArchetype(Archetype a)
        {
            switch (a)
            {
                case Archetype.GroupDuelMatch:
                case Archetype.GroupPracticeMatch:
                    return "PVPGroupDuelMatch";
                case Archetype.GroupDeathMatch:
                    return "PVPGroupDeathMatch";
                case Archetype.GroupDeathMatchUnrated:
                    // Round-robin across the 4 unrated DM zones for instance separation
                    int n = System.Threading.Interlocked.Increment(ref _dmRoundRobin);
                    return $"DeathMatch0{((n & 3) + 1)}"; // DeathMatch01..04
                default: return "PVPGroupDuelMatch";
            }
        }
        private int _dmRoundRobin;

        // ── Per-player queue state ──────────────────────────────────────────
        public enum QueueState
        {
            None,
            Queued,
            Matched,        // assigned to a match, awaiting spawn
            InMatch,        // actively playing
        }

        public class QueueEntry
        {
            public string LoginName;
            public uint CharSqlId;
            public Archetype Archetype;
            public DateTime QueuedAt;
            public int PvpRating;          // for skill-based matchmaking
            public string AssignedMatchId; // null until matched
        }

        public class Match
        {
            public string MatchId;            // generated GUID
            public Archetype Archetype;
            public string ZoneName;           // assigned zone (with instance ID applied at spawn)
            public int InstanceId;           // unique instance for this match
            public List<string> ParticipantLogins = new List<string>();
            public DateTime CreatedAt;
            public DateTime? StartedAt;
            public DateTime? EndedAt;
            public string WinnerLogin;        // for 1v1; for DM the highest-score
            public Dictionary<string, int> KillCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> DeathCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            /// <summary>Match auto-ends after this duration (10 minutes).</summary>
            public const int MaxDurationSec = 600;
            /// <summary>Players must spawn within this many seconds of being matched.</summary>
            public const int SpawnTimeoutSec = 30;

            public bool IsExpired => StartedAt.HasValue
                && (DateTime.UtcNow - StartedAt.Value).TotalSeconds > MaxDurationSec;

            public bool SpawnTimedOut => !StartedAt.HasValue
                && (DateTime.UtcNow - CreatedAt).TotalSeconds > SpawnTimeoutSec;
        }

        // Queue indexed by login name (one queue entry per player max).
        private readonly Dictionary<string, QueueEntry> _queue =
            new Dictionary<string, QueueEntry>(StringComparer.OrdinalIgnoreCase);

        // Active matches indexed by MatchId.
        private readonly Dictionary<string, Match> _activeMatches =
            new Dictionary<string, Match>();

        // Reverse lookup: login → matchId (for fast "what's this player in?" queries).
        private readonly Dictionary<string, string> _playerToMatch =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private readonly object _lock = new object();
        private int _nextInstanceId = 100000; // start high to avoid collision with PvE instances

        // ── Public API ──────────────────────────────────────────────────────

        /// <summary>Add a player to a match queue. Returns false if already queued or in a match.</summary>
        public bool EnqueuePlayer(string loginName, uint charSqlId, Archetype archetype, int pvpRating)
        {
            lock (_lock)
            {
                if (_queue.ContainsKey(loginName))
                {
                    Debug.LogError($"[PVP-MATCH] {loginName} is already queued");
                    return false;
                }
                if (_playerToMatch.ContainsKey(loginName))
                {
                    Debug.LogError($"[PVP-MATCH] {loginName} is already in a match");
                    return false;
                }

                _queue[loginName] = new QueueEntry
                {
                    LoginName = loginName,
                    CharSqlId = charSqlId,
                    Archetype = archetype,
                    QueuedAt = DateTime.UtcNow,
                    PvpRating = pvpRating,
                };
                Debug.LogError($"[PVP-MATCH] {loginName} queued for {archetype} (rating {pvpRating}). " +
                               $"Queue size for archetype: {_queue.Values.Count(e => e.Archetype == archetype)}");
                return true;
            }
        }

        /// <summary>Remove player from queue. Returns true if was queued.</summary>
        public bool DequeuePlayer(string loginName)
        {
            lock (_lock)
            {
                if (!_queue.Remove(loginName))
                    return false;
                Debug.LogError($"[PVP-MATCH] {loginName} removed from queue");
                return true;
            }
        }

        public QueueState GetState(string loginName)
        {
            lock (_lock)
            {
                if (_playerToMatch.TryGetValue(loginName, out var matchId)
                    && _activeMatches.TryGetValue(matchId, out var match))
                {
                    return match.StartedAt.HasValue ? QueueState.InMatch : QueueState.Matched;
                }
                return _queue.ContainsKey(loginName) ? QueueState.Queued : QueueState.None;
            }
        }

        public Match GetMatchForPlayer(string loginName)
        {
            lock (_lock)
            {
                if (_playerToMatch.TryGetValue(loginName, out var matchId)
                    && _activeMatches.TryGetValue(matchId, out var match))
                    return match;
                return null;
            }
        }

        /// <summary>
        /// Run matchmaking pass: pair up queued players into matches when possible.
        /// Returns list of newly-created matches (caller must spawn participants into the assigned zones).
        /// </summary>
        public List<Match> RunMatchmaking()
        {
            var newMatches = new List<Match>();
            lock (_lock)
            {
                // Group queue by archetype
                foreach (Archetype a in Enum.GetValues(typeof(Archetype)))
                {
                    var pool = _queue.Values
                        .Where(e => e.Archetype == a && e.AssignedMatchId == null)
                        .OrderBy(e => e.QueuedAt)   // FIFO base order
                        .ToList();

                    int needed = RequiredPlayers(a);
                    while (pool.Count >= needed)
                    {
                        // Skill-based pairing for ranked: pick the N closest in rating.
                        // Casual: just take the top N from FIFO.
                        List<QueueEntry> chosen;
                        if (IsRanked(a))
                        {
                            // Anchor on the longest-waiting player, then pick (needed-1) closest in rating.
                            var anchor = pool[0];
                            chosen = new List<QueueEntry> { anchor };
                            chosen.AddRange(pool.Skip(1)
                                .OrderBy(e => Math.Abs(e.PvpRating - anchor.PvpRating))
                                .Take(needed - 1));
                        }
                        else
                        {
                            chosen = pool.Take(needed).ToList();
                        }

                        if (chosen.Count < needed) break;

                        var match = CreateMatch(a, chosen);
                        newMatches.Add(match);

                        // Remove matched players from the local pool and the global queue
                        foreach (var p in chosen)
                        {
                            pool.Remove(p);
                            _queue.Remove(p.LoginName);
                        }
                    }
                }
            }

            foreach (var m in newMatches)
                Debug.LogError($"[PVP-MATCH] Created match {m.MatchId} ({m.Archetype}) in zone {m.ZoneName}#{m.InstanceId}: " +
                               string.Join(", ", m.ParticipantLogins));

            return newMatches;
        }

        /// <summary>Mark a match as actually started (all participants have spawned).</summary>
        public void MarkMatchStarted(string matchId)
        {
            lock (_lock)
            {
                if (_activeMatches.TryGetValue(matchId, out var m) && !m.StartedAt.HasValue)
                {
                    m.StartedAt = DateTime.UtcNow;
                    Debug.LogError($"[PVP-MATCH] Match {matchId} STARTED with {m.ParticipantLogins.Count} players");
                }
            }
        }

        /// <summary>
        /// Record a kill in a match. Returns true if the kill ended the match.
        /// For 1v1 matches (Duel/Practice): first kill ends it.
        /// For DM matches: match continues until time limit or all-but-one leave.
        /// </summary>
        public bool RecordKill(string killerLogin, string victimLogin)
        {
            lock (_lock)
            {
                if (!_playerToMatch.TryGetValue(killerLogin, out var matchId))
                    return false;
                if (!_activeMatches.TryGetValue(matchId, out var match) || !match.StartedAt.HasValue)
                    return false;
                if (!match.ParticipantLogins.Contains(victimLogin, StringComparer.OrdinalIgnoreCase))
                    return false;

                if (!match.KillCounts.ContainsKey(killerLogin)) match.KillCounts[killerLogin] = 0;
                if (!match.DeathCounts.ContainsKey(victimLogin)) match.DeathCounts[victimLogin] = 0;
                match.KillCounts[killerLogin]++;
                match.DeathCounts[victimLogin]++;

                Debug.LogError($"[PVP-MATCH] {killerLogin} killed {victimLogin} in match {matchId} " +
                               $"(K/D: {killerLogin}={match.KillCounts[killerLogin]}, victim deaths={match.DeathCounts[victimLogin]})");

                bool isOneVsOne = RequiredPlayers(match.Archetype) == 2;
                if (isOneVsOne)
                {
                    match.WinnerLogin = killerLogin;
                    return true; // first kill wins
                }
                return false;
            }
        }

        /// <summary>End a match and return its final state. Caller is responsible for persisting wins/losses.</summary>
        public Match EndMatch(string matchId, string reason)
        {
            lock (_lock)
            {
                if (!_activeMatches.TryGetValue(matchId, out var match))
                    return null;

                match.EndedAt = DateTime.UtcNow;

                // For DM, winner is whoever has the most kills (tiebreak: fewest deaths)
                if (match.WinnerLogin == null && match.KillCounts.Count > 0)
                {
                    match.WinnerLogin = match.KillCounts
                        .OrderByDescending(kv => kv.Value)
                        .ThenBy(kv => match.DeathCounts.TryGetValue(kv.Key, out var d) ? d : int.MaxValue)
                        .First().Key;
                }

                _activeMatches.Remove(matchId);
                foreach (var login in match.ParticipantLogins)
                    _playerToMatch.Remove(login);

                Debug.LogError($"[PVP-MATCH] Match {matchId} ENDED ({reason}). Winner: {match.WinnerLogin ?? "<none>"}");
                return match;
            }
        }

        /// <summary>
        /// Periodic tick - call from the server's update loop (~once/sec).
        /// Reaps expired/timed-out matches and runs matchmaking.
        /// Returns (newMatches, expiredMatches) for the caller to act on.
        /// </summary>
        public (List<Match> newMatches, List<Match> endedMatches) Tick()
        {
            var ended = new List<Match>();
            lock (_lock)
            {
                foreach (var kv in _activeMatches.ToList())
                {
                    var m = kv.Value;
                    if (m.SpawnTimedOut)
                    {
                        ended.Add(m);
                        _activeMatches.Remove(kv.Key);
                        foreach (var login in m.ParticipantLogins) _playerToMatch.Remove(login);
                        Debug.LogError($"[PVP-MATCH] Match {kv.Key} EXPIRED (spawn timeout)");
                    }
                    else if (m.IsExpired)
                    {
                        m.EndedAt = DateTime.UtcNow;
                        if (m.WinnerLogin == null && m.KillCounts.Count > 0)
                            m.WinnerLogin = m.KillCounts.OrderByDescending(x => x.Value).First().Key;
                        ended.Add(m);
                        _activeMatches.Remove(kv.Key);
                        foreach (var login in m.ParticipantLogins) _playerToMatch.Remove(login);
                        Debug.LogError($"[PVP-MATCH] Match {kv.Key} EXPIRED (time limit)");
                    }
                }
            }

            var newMatches = RunMatchmaking();
            return (newMatches, ended);
        }

        /// <summary>Player disconnected — clean up queue + treat as forfeit if in match.</summary>
        public Match HandleDisconnect(string loginName)
        {
            lock (_lock)
            {
                _queue.Remove(loginName);

                if (_playerToMatch.TryGetValue(loginName, out var matchId)
                    && _activeMatches.TryGetValue(matchId, out var match))
                {
                    // Other participants win if 1v1; for DM just remove this player
                    if (RequiredPlayers(match.Archetype) == 2)
                    {
                        match.WinnerLogin = match.ParticipantLogins
                            .FirstOrDefault(p => !p.Equals(loginName, StringComparison.OrdinalIgnoreCase));
                        return EndMatch(matchId, $"{loginName} disconnected");
                    }
                    else
                    {
                        match.ParticipantLogins.Remove(loginName);
                        _playerToMatch.Remove(loginName);
                        if (match.ParticipantLogins.Count <= 1)
                        {
                            match.WinnerLogin = match.ParticipantLogins.FirstOrDefault();
                            return EndMatch(matchId, "all but one player left");
                        }
                    }
                }
                return null;
            }
        }

        // ── Internal ────────────────────────────────────────────────────────
        private Match CreateMatch(Archetype a, List<QueueEntry> participants)
        {
            var matchId = Guid.NewGuid().ToString("N").Substring(0, 12);
            var instanceId = System.Threading.Interlocked.Increment(ref _nextInstanceId);
            var match = new Match
            {
                MatchId = matchId,
                Archetype = a,
                ZoneName = PickZoneForArchetype(a),
                InstanceId = instanceId,
                CreatedAt = DateTime.UtcNow,
                ParticipantLogins = participants.Select(p => p.LoginName).ToList(),
            };
            _activeMatches[matchId] = match;
            foreach (var p in participants)
            {
                _playerToMatch[p.LoginName] = matchId;
                p.AssignedMatchId = matchId;
            }
            return match;
        }

        // ── Diagnostic / introspection ─────────────────────────────────────
        public int TotalQueued => _queue.Count;
        public int TotalActiveMatches => _activeMatches.Count;
        public IEnumerable<Match> GetActiveMatches()
        {
            lock (_lock) return _activeMatches.Values.ToList();
        }
    }
}