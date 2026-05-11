using System.Collections.Generic;
using UnityEngine;

namespace DungeonRunners.Core
{
    /// <summary>
    /// Global session token management for auth-to-game server handoff
    /// </summary>
    public static class GlobalSessions
    {
        private static readonly Dictionary<uint, string> _sessions = new Dictionary<uint, string>();
        private static readonly object _lock = new object();

        /// <summary>
        /// Store a session token for a user
        /// </summary>
        public static void Store(uint token, string username)
        {
            lock (_lock)
            {
                _sessions[token] = username;
                Debug.Log($"[GlobalSessions] Stored session token 0x{token:X8} for user '{username}'");
            }
        }

        /// <summary>
        /// Try to consume (retrieve and remove) a session token
        /// Returns true if token was valid, false otherwise
        /// </summary>
        public static bool TryConsume(uint token, out string username)
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(token, out username))
                {
                    _sessions.Remove(token);
                    Debug.Log($"[GlobalSessions] Consumed session token 0x{token:X8} for user '{username}'");
                    return true;
                }
                
                Debug.LogWarning($"[GlobalSessions] Invalid or expired session token 0x{token:X8}");
                username = null;
                return false;
            }
        }

        /// <summary>
        /// Check if a token exists without consuming it
        /// </summary>
        public static bool Exists(uint token)
        {
            lock (_lock)
            {
                return _sessions.ContainsKey(token);
            }
        }

        /// <summary>
        /// Clear all sessions (for testing/cleanup)
        /// </summary>
        public static void Clear()
        {
            lock (_lock)
            {
                int count = _sessions.Count;
                _sessions.Clear();
                Debug.Log($"[GlobalSessions] Cleared {count} session tokens");
            }
        }

        /// <summary>
        /// Get current session count (for monitoring)
        /// </summary>
        public static int Count
        {
            get
            {
                lock (_lock)
                {
                    return _sessions.Count;
                }
            }
        }
    }
}