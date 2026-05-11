using System;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Mono.Data.Sqlite;

namespace DungeonRunners.Database
{
    /// <summary>
    /// Account management — proper auth with password hashing.
    /// Replaces the in-memory SessionManager for persistent accounts.
    /// </summary>
    public static class AccountRepository
    {
        /// <summary>Create a new account. Returns account ID or 0 on failure.</summary>
        public static uint CreateAccount(string username, string password)
        {
            Debug.LogError($"[DB-AUTH] CreateAccount called: username='{username}' password='{(string.IsNullOrEmpty(password) ? "EMPTY" : "***")}'");

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                Debug.LogError($"[DB-AUTH] REJECTED: username or password is empty!");
                return 0;
            }

            string salt = GenerateSalt();
            string hash = HashPassword(password, salt);

            try
            {
                Debug.LogError($"[DB-AUTH] DB Path: {GameDatabase.DbPath}");
                using (var conn = GameDatabase.GetConnection())
                {
                    Debug.LogError($"[DB-AUTH] Got connection, inserting account...");
                    GameDatabase.ExecuteNonQuery(conn,
                        "INSERT INTO accounts (username, password_hash, salt) VALUES (@u, @h, @s)",
                        ("@u", username), ("@h", hash), ("@s", salt));

                    Debug.LogError($"[DB-AUTH] INSERT done, getting ID...");
                    object id = GameDatabase.ExecuteScalar(conn, "SELECT last_insert_rowid()");
                    uint accountId = Convert.ToUInt32(id);
                    Debug.LogError($"[DB-AUTH] ✅ Created account '{username}' (ID: {accountId})");
                    return accountId;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DB-AUTH] ❌ CreateAccount FAILED: {ex.GetType().Name}: {ex.Message}");
                Debug.LogError($"[DB-AUTH] ❌ Stack: {ex.StackTrace}");

                if (ex.Message.Contains("UNIQUE") || ex.Message.Contains("unique"))
                    Debug.LogError($"[DB-AUTH] Username '{username}' already exists");

                return 0;
            }
        }

        /// <summary>
        /// Authenticate a user. Returns account ID or 0 on failure.
        /// If account doesn't exist and auto_create is true, creates it.
        /// </summary>
        public static uint Authenticate(string username, string password, bool autoCreate = true)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    using (var reader = GameDatabase.ExecuteReader(conn,
                        "SELECT id, password_hash, salt, is_banned FROM accounts WHERE username = @u",
                        ("@u", username)))
                    {
                        if (reader.Read())
                        {
                            // Account exists — verify password
                            uint id = (uint)reader.GetInt32(0);
                            string storedHash = reader.GetString(1);
                            string salt = reader.GetString(2);
                            bool isBanned = reader.GetInt32(3) != 0;

                            if (isBanned)
                            {
                                Debug.LogError($"[DB-AUTH] Account '{username}' is banned");
                                return 0;
                            }

                            string checkHash = HashPassword(password, salt);
                            if (checkHash != storedHash)
                            {
                                Debug.LogError($"[DB-AUTH] Wrong password for '{username}'");
                                return 0;
                            }

                            // Update last login
                            reader.Close();
                            GameDatabase.ExecuteNonQuery(conn,
                                "UPDATE accounts SET last_login = datetime('now') WHERE id = @id",
                                ("@id", (int)id));

                            Debug.LogError($"[DB-AUTH] Authenticated '{username}' (ID: {id})");
                            return id;
                        }
                    }

                    // Account doesn't exist
                    if (autoCreate)
                    {
                        Debug.LogError($"[DB-AUTH] Auto-creating account '{username}'");
                        return CreateAccount(username, password);
                    }

                    Debug.LogError($"[DB-AUTH] Account '{username}' not found");
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DB-AUTH] Auth error: {ex.Message}");
                return 0;
            }
        }

        /// <summary>Check if username is taken.</summary>
        public static bool UsernameExists(string username)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    object result = GameDatabase.ExecuteScalar(conn,
                        "SELECT COUNT(*) FROM accounts WHERE username = @u",
                        ("@u", username));
                    return Convert.ToInt32(result) > 0;
                }
            }
            catch { return false; }
        }

        /// <summary>Get account ID by username.</summary>
        public static uint GetAccountId(string username)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    object result = GameDatabase.ExecuteScalar(conn,
                        "SELECT id FROM accounts WHERE username = @u",
                        ("@u", username));
                    return result != null ? Convert.ToUInt32(result) : 0;
                }
            }
            catch { return 0; }
        }

        /// <summary>Ban or unban an account.</summary>
        public static void SetBanned(uint accountId, bool banned)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(conn,
                        "UPDATE accounts SET is_banned = @b WHERE id = @id",
                        ("@b", banned ? 1 : 0), ("@id", (int)accountId));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DB-AUTH] Ban error: {ex.Message}");
            }
        }

        // ═══════════════════════════════════════════════════════════
        // PASSWORD HASHING — SHA256 with per-account salt
        // ═══════════════════════════════════════════════════════════

        private static string GenerateSalt()
        {
            byte[] saltBytes = new byte[16];
            using (var rng = new RNGCryptoServiceProvider())
                rng.GetBytes(saltBytes);
            return Convert.ToBase64String(saltBytes);
        }

        private static string HashPassword(string password, string salt)
        {
            using (var sha256 = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(salt + password);
                byte[] hash = sha256.ComputeHash(bytes);
                return Convert.ToBase64String(hash);
            }
        }
    }
}
