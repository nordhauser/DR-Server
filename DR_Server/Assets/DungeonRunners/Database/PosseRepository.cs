using System;
using UnityEngine;
using Mono.Data.Sqlite;

namespace DungeonRunners.Database
{
    public class PosseRecord
    {
        public uint Id;
        public string Name;
        public uint FounderCharacterId;
        public uint GoldPaid;
        public string FoundedAt;
        public string Motd;
        public string Description;
    }

    public static class PosseRepository
    {
        public static bool PosseNameExists(string name)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    object count = GameDatabase.ExecuteScalar(conn,
                        "SELECT COUNT(*) FROM posses WHERE name = @n", ("@n", name));
                    return Convert.ToInt32(count) > 0;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] PosseNameExists error: {ex.Message}");
                return false;
            }
        }

        public static PosseRecord CreatePosse(string name, uint founderCharacterId, uint goldPaid)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                using (var tx = conn.BeginTransaction())
                {
                    GameDatabase.ExecuteNonQuery(conn,
                        @"INSERT INTO posses (name, founder_character_id, gold_paid)
                          VALUES (@n, @fid, @g)",
                        ("@n", name), ("@fid", (int)founderCharacterId), ("@g", (int)goldPaid));

                    int posseId = Convert.ToInt32(GameDatabase.ExecuteScalar(conn, "SELECT last_insert_rowid()"));

                    GameDatabase.ExecuteNonQuery(conn,
                        "UPDATE characters SET posse_id = @pid WHERE id = @cid",
                        ("@pid", posseId), ("@cid", (int)founderCharacterId));

                    tx.Commit();
                    Debug.LogError($"[POSSE-DB] Created posse '{name}' id={posseId} founder={founderCharacterId} gold={goldPaid}");
                    return new PosseRecord
                    {
                        Id = (uint)posseId,
                        Name = name,
                        FounderCharacterId = founderCharacterId,
                        GoldPaid = goldPaid,
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] CreatePosse error: {ex.Message}");
                return null;
            }
        }

        public static PosseRecord GetPosse(uint posseId)
        {
            if (posseId == 0) return null;
            try
            {
                using (var conn = GameDatabase.GetConnection())
                using (var r = GameDatabase.ExecuteReader(conn,
                    "SELECT id, name, founder_character_id, gold_paid, founded_at, COALESCE(motd,''), COALESCE(description,'') FROM posses WHERE id = @id",
                    ("@id", (int)posseId)))
                {
                    if (!r.Read()) return null;
                    return new PosseRecord
                    {
                        Id = (uint)r.GetInt32(0),
                        Name = r.GetString(1),
                        FounderCharacterId = (uint)r.GetInt32(2),
                        GoldPaid = (uint)r.GetInt32(3),
                        FoundedAt = r.IsDBNull(4) ? "" : r.GetString(4),
                        Motd = r.GetString(5),
                        Description = r.GetString(6),
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] GetPosse error: {ex.Message}");
                return null;
            }
        }

        public static bool Rename(uint posseId, string newName)
        {
            if (posseId == 0 || string.IsNullOrEmpty(newName)) return false;
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(conn,
                        "UPDATE posses SET name = @n WHERE id = @id",
                        ("@id", (int)posseId), ("@n", newName));
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] Rename error: {ex.Message}");
                return false;
            }
        }

        public static bool SetMotd(uint posseId, string motd)
        {
            if (posseId == 0) return false;
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(conn,
                        "UPDATE posses SET motd = @m WHERE id = @id",
                        ("@id", (int)posseId), ("@m", motd ?? ""));
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] SetMotd error: {ex.Message}");
                return false;
            }
        }

        public static bool SetDescription(uint posseId, string desc)
        {
            if (posseId == 0) return false;
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(conn,
                        "UPDATE posses SET description = @d WHERE id = @id",
                        ("@id", (int)posseId), ("@d", desc ?? ""));
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] SetDescription error: {ex.Message}");
                return false;
            }
        }

        public static int MemberCount(uint posseId)
        {
            if (posseId == 0) return 0;
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    object n = GameDatabase.ExecuteScalar(conn,
                        "SELECT COUNT(*) FROM characters WHERE posse_id = @pid", ("@pid", (int)posseId));
                    return Convert.ToInt32(n);
                }
            }
            catch { return 0; }
        }

        // List member character names for a posse. Used by /posse members chat echo.
        public static System.Collections.Generic.List<string> MemberNames(uint posseId)
        {
            var result = new System.Collections.Generic.List<string>();
            if (posseId == 0) return result;
            try
            {
                using (var conn = GameDatabase.GetConnection())
                using (var r = GameDatabase.ExecuteReader(conn,
                    "SELECT name FROM characters WHERE posse_id = @pid ORDER BY id",
                    ("@pid", (int)posseId)))
                {
                    while (r.Read())
                        result.Add(r.GetString(0));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] MemberNames error: {ex.Message}");
            }
            return result;
        }

        // Clears a single character's posse_id (used by /posse leave for non-last-member exit).
        public static void ClearCharacterMembership(uint characterId, int cooldownExpiresUnix)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(conn,
                        "UPDATE characters SET posse_id = 0, posse_join_cooldown = @cd WHERE id = @cid",
                        ("@cid", (int)characterId), ("@cd", cooldownExpiresUnix));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] ClearCharacterMembership error: {ex.Message}");
            }
        }

        // Deletes a posse row + clears posse_id from every member, transactionally.
        // Used by /posse disband and by the last-member-leaves path. Returns the
        // names of the affected characters so callers can notify them.
        public static System.Collections.Generic.List<string> DisbandPosse(uint posseId, int cooldownExpiresUnix)
        {
            var memberNames = new System.Collections.Generic.List<string>();
            if (posseId == 0) return memberNames;
            try
            {
                using (var conn = GameDatabase.GetConnection())
                using (var tx = conn.BeginTransaction())
                {
                    using (var r = GameDatabase.ExecuteReader(conn,
                        "SELECT name FROM characters WHERE posse_id = @pid",
                        ("@pid", (int)posseId)))
                    {
                        while (r.Read()) memberNames.Add(r.GetString(0));
                    }
                    GameDatabase.ExecuteNonQuery(conn,
                        "UPDATE characters SET posse_id = 0, posse_join_cooldown = @cd WHERE posse_id = @pid",
                        ("@pid", (int)posseId), ("@cd", cooldownExpiresUnix));
                    GameDatabase.ExecuteNonQuery(conn,
                        "DELETE FROM posses WHERE id = @pid", ("@pid", (int)posseId));
                    tx.Commit();
                    Debug.LogError($"[POSSE-DB] Disbanded posse id={posseId} (members affected={memberNames.Count})");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[POSSE-DB] DisbandPosse error: {ex.Message}");
            }
            return memberNames;
        }
    }
}
