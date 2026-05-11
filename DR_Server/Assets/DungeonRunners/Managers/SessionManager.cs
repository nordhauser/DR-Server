using System.Collections.Generic;
using UnityEngine;

namespace DungeonRunners.Managers
{
    /// <summary>
    /// Manages user sessions across auth and game servers
    /// </summary>
    public class SessionManager : MonoBehaviour
    {
        private static SessionManager _instance;
        public static SessionManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("SessionManager");
                    _instance = go.AddComponent<SessionManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private Dictionary<uint, UserSession> _sessions = new Dictionary<uint, UserSession>();
        private Dictionary<string, uint> _usernameToAccountId = new Dictionary<string, uint>();
        private Dictionary<uint, string> _playTokens = new Dictionary<uint, string>();
        private uint _nextAccountId = 1;
        private readonly object _lock = new object();

        void Awake()
        {
            if (_instance == null)
            {
                _instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else if (_instance != this)
            {
                Destroy(gameObject);
            }
        }

        public UserSession CreateSession(string username, string password)
        {
            lock (_lock)
            {
                // Check if user already exists
                if (_usernameToAccountId.TryGetValue(username, out uint existingAccountId))
                {
                    if (_sessions.TryGetValue(existingAccountId, out UserSession existingSession))
                    {
                        if (existingSession.Password == password)
                        {
                            existingSession.IsAuthenticated = true;
                            existingSession.LastLoginTime = System.DateTime.Now;
                            Debug.Log($"User logged in: {username} (Account ID: {existingAccountId})");
                            return existingSession;
                        }
                        else
                        {
                            Debug.LogWarning($"Invalid password for user: {username}");
                            return null;
                        }
                    }
                }

                // Create new account
                uint accountId = _nextAccountId++;
                var session = new UserSession
                {
                    AccountId = accountId,
                    Username = username,
                    Password = password,
                    IsAuthenticated = true,
                    CreatedTime = System.DateTime.Now,
                    LastLoginTime = System.DateTime.Now
                };

                _sessions[accountId] = session;
                _usernameToAccountId[username] = accountId;

                Debug.Log($"Created new account: {username} (Account ID: {accountId})");
                return session;
            }
        }

        public UserSession GetSession(uint accountId)
        {
            lock (_lock)
            {
                return _sessions.ContainsKey(accountId) ? _sessions[accountId] : null;
            }
        }

        public UserSession GetSessionByUsername(string username)
        {
            lock (_lock)
            {
                if (_usernameToAccountId.TryGetValue(username, out uint accountId))
                {
                    return GetSession(accountId);
                }
                return null;
            }
        }

        public void SetSelectedCharacter(uint accountId, uint characterId)
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(accountId, out UserSession session))
                {
                    session.SelectedCharacterId = characterId;
                    Debug.Log($"Account {accountId} selected character {characterId}");
                }
            }
        }

        public void RemoveSession(uint accountId)
        {
            lock (_lock)
            {
                if (_sessions.TryGetValue(accountId, out UserSession session))
                {
                    _usernameToAccountId.Remove(session.Username);
                    _sessions.Remove(accountId);
                    Debug.Log($"Removed session for account: {accountId}");
                }
            }
        }

        public void SetPlayToken(uint playToken, string username)
        {
            lock (_lock)
            {
                _playTokens[playToken] = username;
                Debug.Log($"Set play token 0x{playToken:X8} for user: {username}");
            }
        }

        public bool ValidatePlayToken(uint playToken, out string username)
        {
            lock (_lock)
            {
                return _playTokens.TryGetValue(playToken, out username);
            }
        }

        public void RemovePlayToken(uint playToken)
        {
            lock (_lock)
            {
                if (_playTokens.Remove(playToken))
                {
                    Debug.Log($"Removed play token 0x{playToken:X8}");
                }
            }
        }

        public uint GetAccountId(string username)
        {
            lock (_lock)
            {
                return _usernameToAccountId.TryGetValue(username, out uint accountId) ? accountId : 0;
            }
        }
    }

    [System.Serializable]
    public class UserSession
    {
        public uint AccountId;
        public string Username;
        public string Password;
        public bool IsAuthenticated;
        public uint SelectedCharacterId;
        public System.DateTime CreatedTime;
        public System.DateTime LastLoginTime;
    }
}
