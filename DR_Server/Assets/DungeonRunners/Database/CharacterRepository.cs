using System;
using System.Collections.Generic;
using UnityEngine;
using Mono.Data.Sqlite;
using DungeonRunners.Data;

namespace DungeonRunners.Database
{
    /// <summary>
    /// Character CRUD backed by SQLite.
    /// Replaces ClassConfig.SaveCharacter/GetCharacter/CreateNewCharacter/DeleteCharacter.
    /// </summary>
    public static class CharacterRepository
    {
        // ═══════════════════════════════════════════════════════════
        // CREATE
        // ═══════════════════════════════════════════════════════════

        public static SavedCharacter CreateCharacter(string name, string className, uint accountId, string accountName, string avatarClass = "")
        {
            // Check name uniqueness
            if (CharacterNameExists(name))
            {
                Debug.LogError($"[DB-CHAR] Name '{name}' already taken");
                return null;
            }

            var classDef = ClassConfig.GetClassDefinition(className);
            if (classDef == null)
            {
                Debug.LogError($"[DB-CHAR] Invalid class: {className}");
                return null;
            }

            try
            {
                using (var conn = GameDatabase.GetConnection())
                using (var tx = conn.BeginTransaction())
                {
                    // Insert character
                    GameDatabase.ExecuteNonQuery(conn,
                        @"INSERT INTO characters (account_id, name, class_name, avatar_class, level, experience, gold, current_zone)
                          VALUES (@aid, @name, @class, @avatar, 1, 0, 100, 'tutorial')",
                        ("@aid", (int)accountId), ("@name", name), ("@class", className), ("@avatar", avatarClass));

                    int charId = Convert.ToInt32(GameDatabase.ExecuteScalar(conn, "SELECT last_insert_rowid()"));

                    // Insert starting equipment
                    var equip = classDef.startingEquipment;
                    InsertEquipment(conn, charId, "weapon", equip.weapon);
                    InsertEquipment(conn, charId, "armor", equip.armor);
                    InsertEquipment(conn, charId, "helmet", equip.helmet);
                    InsertEquipment(conn, charId, "gloves", equip.gloves);
                    InsertEquipment(conn, charId, "boots", equip.boots);
                    InsertEquipment(conn, charId, "shoulders", equip.shoulders ?? "");
                    InsertEquipment(conn, charId, "shield", equip.shield ?? "");
                    InsertEquipment(conn, charId, "ring1", equip.ring1 ?? "");
                    InsertEquipment(conn, charId, "ring2", equip.ring2 ?? "");
                    InsertEquipment(conn, charId, "amulet", equip.amulet ?? "");

                    // Insert starting skills
                    if (classDef.startingSkills != null)
                    {
                        foreach (var skill in classDef.startingSkills)
                        {
                            int hotbarSlot = GetStartingSkillHotbarSlot(className, skill);
                            GameDatabase.ExecuteNonQuery(conn,
                                "INSERT INTO character_skills (character_id, skill_gc_class, level, hotbar_slot) VALUES (@cid, @s, 1, @h)",
                                ("@cid", charId), ("@s", skill), ("@h", hotbarSlot));
                        }
                    }

                    // Insert starting inventory
                    if (classDef.startingInventory != null)
                    {
                        foreach (var item in classDef.startingInventory)
                        {
                            int count = item.count > 0 ? item.count : 1;
                            GameDatabase.ExecuteNonQuery(conn,
                                "INSERT INTO character_inventory (character_id, gc_class, slot_x, slot_y, count) VALUES (@cid, @gc, @x, @y, @count)",
                                ("@cid", charId), ("@gc", item.gcClass), ("@x", (int)item.x), ("@y", (int)item.y), ("@count", count));
                        }
                    }

                    // Bling Gnome skill book — given to ALL new characters
                    GameDatabase.ExecuteNonQuery(conn,
                        "INSERT INTO character_inventory (character_id, gc_class, slot_x, slot_y, count, rarity, stored_level) VALUES (@cid, @gc, @x, @y, 1, @r, @sl)",
                        ("@cid", charId), ("@gc", "SkillBookPAL.SummonBlingGnome"), ("@x", 0), ("@y", 2), ("@r", 5), ("@sl", 1));
                    // Also learn the skill so it can be placed on hotbar
                    GameDatabase.ExecuteNonQuery(conn,
                        "INSERT INTO character_skills (character_id, skill_gc_class, level) VALUES (@cid, @s, 1)",
                        ("@cid", charId), ("@s", "skills.generic.SummonBlingGnome"));

                    tx.Commit();
                    Debug.LogError($"[DB-CHAR] Created '{name}' (ID: {charId}) class={className} account={accountId}");

                    // Return as SavedCharacter for compatibility
                    return GetCharacter((uint)charId);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DB-CHAR] Create error: {ex.Message}");
                return null;
            }
        }

        private static int GetStartingSkillHotbarSlot(string className, string skill)
        {
            string cls = (className ?? "").ToLowerInvariant();
            string s = skill ?? "";

            if (cls.Contains("fight") || cls.Contains("warrior"))
            {
                if (string.Equals(s, "skills.generic.Stomp", StringComparison.OrdinalIgnoreCase)) return 100;
                if (string.Equals(s, "skills.generic.Butcher", StringComparison.OrdinalIgnoreCase)) return 105;
                if (string.Equals(s, "skills.generic.FighterClassPassive", StringComparison.OrdinalIgnoreCase)) return 108;
                if (string.Equals(s, "skills.generic.MeleeAttackSpeedModPassive", StringComparison.OrdinalIgnoreCase)) return 109;
            }
            else if (cls.Contains("ranger"))
            {
                if (string.Equals(s, "skills.generic.PoisonBlastRadius", StringComparison.OrdinalIgnoreCase)) return 100;
                if (string.Equals(s, "skills.generic.PoisonShot", StringComparison.OrdinalIgnoreCase)) return 105;
                if (string.Equals(s, "skills.generic.RangerClassPassive", StringComparison.OrdinalIgnoreCase)) return 108;
                if (string.Equals(s, "skills.generic.RangeAttackSpeedModPassive", StringComparison.OrdinalIgnoreCase)) return 109;
            }
            else if (cls.Contains("mage") || cls.Contains("warlock"))
            {
                if (string.Equals(s, "skills.generic.ShadowLightning", StringComparison.OrdinalIgnoreCase)) return 100;
                if (string.Equals(s, "skills.generic.FireBolt", StringComparison.OrdinalIgnoreCase)) return 105;
                if (string.Equals(s, "skills.generic.MageClassPassive", StringComparison.OrdinalIgnoreCase)) return 108;
                if (string.Equals(s, "skills.generic.MagicDamageModPassive", StringComparison.OrdinalIgnoreCase)) return 109;
            }

            return -1;
        }

        // ═══════════════════════════════════════════════════════════
        // READ
        // ═══════════════════════════════════════════════════════════

        public static SavedCharacter GetCharacter(uint characterId)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    SavedCharacter ch = null;

                    using (var r = GameDatabase.ExecuteReader(conn,
                        "SELECT * FROM characters WHERE id = @id", ("@id", (int)characterId)))
                    {
                        if (!r.Read()) return null;
                        ch = ReadCharacterRow(r);
                    }

                    // Load sub-tables
                    ch.equipment = LoadEquipment(conn, (int)characterId);
                    ch.skills = LoadSkillList(conn, (int)characterId);
                    ch.skillLevels = LoadSkillLevels(conn, (int)characterId);
                    ch.hotbarSlots = LoadHotbarSlots(conn, (int)characterId);
                    ch.inventory = LoadInventory(conn, (int)characterId);
                    ch.activeQuests = LoadActiveQuests(conn, (int)characterId);
                    ch.completedQuests = LoadCompletedQuests(conn, (int)characterId);
                    ch.unlockedCheckpoints = LoadCheckpoints(conn, (int)characterId);

                    return ch;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DB-CHAR] GetCharacter error: {ex.Message}");
                return null;
            }
        }

        public static SavedCharacter GetCharacterByName(string accountName, string characterName)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    object id = GameDatabase.ExecuteScalar(conn,
                        "SELECT id FROM characters WHERE name = @n", ("@n", characterName));
                    if (id == null) return null;
                    return GetCharacter(Convert.ToUInt32(id));
                }
            }
            catch { return null; }
        }

        public static List<SavedCharacter> GetCharactersForAccount(string accountName)
        {
            var result = new List<SavedCharacter>();
            try
            {
                uint accountId = AccountRepository.GetAccountId(accountName);
                if (accountId == 0) return result;

                using (var conn = GameDatabase.GetConnection())
                {
                    var ids = new List<int>();
                    using (var r = GameDatabase.ExecuteReader(conn,
                        "SELECT id FROM characters WHERE account_id = @aid",
                        ("@aid", (int)accountId)))
                    {
                        while (r.Read())
                            ids.Add(r.GetInt32(0));
                    }

                    foreach (int id in ids)
                        result.Add(GetCharacter((uint)id));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DB-CHAR] GetForAccount error: {ex.Message}");
            }
            return result;
        }

        public static bool CharacterNameExists(string name)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    object count = GameDatabase.ExecuteScalar(conn,
                        "SELECT COUNT(*) FROM characters WHERE name = @n", ("@n", name));
                    return Convert.ToInt32(count) > 0;
                }
            }
            catch { return false; }
        }

        // ═══════════════════════════════════════════════════════════
        // UPDATE (SaveCharacter replacement)
        // ═══════════════════════════════════════════════════════════

        public static void SaveCharacter(SavedCharacter ch)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                using (var tx = conn.BeginTransaction())
                {
                    // Update main character row
                    GameDatabase.ExecuteNonQuery(conn, @"
                        UPDATE characters SET
                            level = @lv, experience = @xp, gold = @g,
                            avatar_class = @ac,
                            skin = @sk, face = @fc, face_feature = @ff,
                            hair = @hr, hair_color = @hc,
                            current_zone = @zone, zone_id = @zid,
                            position_x = @px, position_y = @py, position_z = @pz,
                            current_hp = @hp, current_mana = @mp,
                            max_hp = @mxhp, max_mana = @mxmp,
                            stat_strength = @str, stat_agility = @agi,
                            stat_intellect = @int, stat_endurance = @end,
                            last_respec_time = @lrt,
                            respec_count = @rsc, pvp_wins = @pvpw, pvp_rating = @pvpr,
                            tp_zone = @tpz, tp_zone_id = @tpzid, tp_target_zone = @tptz,
                            tp_pos_x = @tppx, tp_pos_y = @tppy, tp_pos_z = @tppz
                        WHERE id = @id",
                        ("@id", (int)ch.id), ("@lv", (int)ch.level), ("@xp", (int)ch.experience),
                        ("@g", (int)ch.gold), ("@ac", ch.avatarClass ?? ""),
                        ("@sk", (int)ch.skin), ("@fc", (int)ch.face),
                        ("@ff", (int)ch.faceFeature), ("@hr", (int)ch.hair), ("@hc", (int)ch.hairColor),
                        ("@zone", ch.currentZoneName ?? "tutorial"), ("@zid", ch.zoneId),
                        ("@px", ch.position.x), ("@py", ch.position.y), ("@pz", ch.position.z),
                        ("@hp", (int)ch.currentHP), ("@mp", (int)ch.currentMana),
                        ("@mxhp", ch.maxHP), ("@mxmp", ch.maxMana),
                        ("@str", ch.statStrength), ("@agi", ch.statAgility),
                        ("@int", ch.statIntellect), ("@end", ch.statEndurance),
                        ("@lrt", ch.lastRespecTime),
                        ("@rsc", ch.respecCount), ("@pvpw", ch.pvpWins), ("@pvpr", ch.pvpRating),
                        ("@tpz", ch.tpZone ?? ""), ("@tpzid", ch.tpZoneId),
                        ("@tptz", ch.tpTargetZone ?? ""),
                        ("@tppx", ch.tpPosX), ("@tppy", ch.tpPosY), ("@tppz", ch.tpPosZ));

                    // Replace equipment
                    GameDatabase.ExecuteNonQuery(conn, "DELETE FROM character_equipment WHERE character_id = @cid", ("@cid", (int)ch.id));
                    if (ch.equipment != null)
                    {
                        var sr = ch.equipment.slotRarity ?? new Dictionary<string, int>();
                        var sl = ch.equipment.slotLevel ?? new Dictionary<string, int>();
                        int rv; int lv;
                        InsertEquipment(conn, (int)ch.id, "weapon", ch.equipment.weapon, sr.TryGetValue("weapon", out rv) ? rv : -1, sl.TryGetValue("weapon", out lv) ? lv : -1);
                        InsertEquipment(conn, (int)ch.id, "armor", ch.equipment.armor, sr.TryGetValue("armor", out rv) ? rv : -1, sl.TryGetValue("armor", out lv) ? lv : -1);
                        InsertEquipment(conn, (int)ch.id, "helmet", ch.equipment.helmet, sr.TryGetValue("helmet", out rv) ? rv : -1, sl.TryGetValue("helmet", out lv) ? lv : -1);
                        InsertEquipment(conn, (int)ch.id, "gloves", ch.equipment.gloves, sr.TryGetValue("gloves", out rv) ? rv : -1, sl.TryGetValue("gloves", out lv) ? lv : -1);
                        InsertEquipment(conn, (int)ch.id, "boots", ch.equipment.boots, sr.TryGetValue("boots", out rv) ? rv : -1, sl.TryGetValue("boots", out lv) ? lv : -1);
                        InsertEquipment(conn, (int)ch.id, "shoulders", ch.equipment.shoulders ?? "", sr.TryGetValue("shoulders", out rv) ? rv : -1, sl.TryGetValue("shoulders", out lv) ? lv : -1);
                        InsertEquipment(conn, (int)ch.id, "shield", ch.equipment.shield ?? "", sr.TryGetValue("shield", out rv) ? rv : -1, sl.TryGetValue("shield", out lv) ? lv : -1);
                        InsertEquipment(conn, (int)ch.id, "ring1", ch.equipment.ring1 ?? "", sr.TryGetValue("ring1", out rv) ? rv : -1, sl.TryGetValue("ring1", out lv) ? lv : -1);
                        InsertEquipment(conn, (int)ch.id, "ring2", ch.equipment.ring2 ?? "", sr.TryGetValue("ring2", out rv) ? rv : -1, sl.TryGetValue("ring2", out lv) ? lv : -1);
                        InsertEquipment(conn, (int)ch.id, "amulet", ch.equipment.amulet ?? "", sr.TryGetValue("amulet", out rv) ? rv : -1, sl.TryGetValue("amulet", out lv) ? lv : -1);
                    }

                    // Replace inventory
                    GameDatabase.ExecuteNonQuery(conn, "DELETE FROM character_inventory WHERE character_id = @cid", ("@cid", (int)ch.id));
                    if (ch.inventory != null)
                    {
                        foreach (var item in ch.inventory)
                        {
                            GameDatabase.ExecuteNonQuery(conn,
                                "INSERT INTO character_inventory (character_id, gc_class, slot_x, slot_y, count, buy_price, rarity, stored_level) VALUES (@cid, @gc, @x, @y, @c, @bp, @r, @sl)",
                                ("@cid", (int)ch.id), ("@gc", item.gcClass), ("@x", (int)item.x), ("@y", (int)item.y), ("@c", item.count), ("@bp", (int)item.buyPrice), ("@r", item.rarity), ("@sl", item.storedLevel));
                        }
                    }

                    // Replace skills
                    GameDatabase.ExecuteNonQuery(conn, "DELETE FROM character_skills WHERE character_id = @cid", ("@cid", (int)ch.id));
                    if (ch.skills != null)
                    {
                        foreach (var skill in ch.skills)
                        {
                            int level = ch.GetSkillLevel(skill);
                            int hotbar = -1;
                            if (ch.hotbarSlots != null)
                            {
                                foreach (var hs in ch.hotbarSlots)
                                    if (hs.skill == skill) { hotbar = (int)hs.slot; break; }
                            }
                            GameDatabase.ExecuteNonQuery(conn,
                                "INSERT INTO character_skills (character_id, skill_gc_class, level, hotbar_slot) VALUES (@cid, @s, @l, @h)",
                                ("@cid", (int)ch.id), ("@s", skill), ("@l", level), ("@h", hotbar));
                        }
                    }

                    // Replace active quests
                    GameDatabase.ExecuteNonQuery(conn, "DELETE FROM character_quests WHERE character_id = @cid AND status = 'active'", ("@cid", (int)ch.id));
                    GameDatabase.ExecuteNonQuery(conn, "DELETE FROM quest_objectives WHERE character_id = @cid", ("@cid", (int)ch.id));
                    if (ch.activeQuests != null)
                    {
                        foreach (var q in ch.activeQuests)
                        {
                            GameDatabase.ExecuteNonQuery(conn,
                                "INSERT OR REPLACE INTO character_quests (character_id, quest_id, quest_giver_id, accepted_at, status) VALUES (@cid, @qid, @gid, @at, 'active')",
                                ("@cid", (int)ch.id), ("@qid", q.questId), ("@gid", q.questGiverId ?? ""), ("@at", q.acceptedAt ?? ""));

                            if (q.objectives != null)
                            {
                                foreach (var obj in q.objectives)
                                {
                                    GameDatabase.ExecuteNonQuery(conn,
                                        "INSERT INTO quest_objectives (character_id, quest_id, objective_name, type, target, label, required, current) VALUES (@cid, @qid, @on, @t, @tgt, @lb, @req, @cur)",
                                        ("@cid", (int)ch.id), ("@qid", q.questId), ("@on", obj.objectiveName ?? ""),
                                        ("@t", obj.type ?? ""), ("@tgt", obj.target ?? ""), ("@lb", obj.label ?? ""),
                                        ("@req", obj.required), ("@cur", obj.current));
                                }
                            }
                        }
                    }

                    // Replace completed quests
                    GameDatabase.ExecuteNonQuery(conn, "DELETE FROM completed_quests WHERE character_id = @cid", ("@cid", (int)ch.id));
                    if (ch.completedQuests != null)
                    {
                        foreach (var qid in ch.completedQuests)
                        {
                            GameDatabase.ExecuteNonQuery(conn,
                                "INSERT OR IGNORE INTO completed_quests (character_id, quest_id) VALUES (@cid, @qid)",
                                ("@cid", (int)ch.id), ("@qid", qid));
                        }
                    }

                    // Replace checkpoints
                    GameDatabase.ExecuteNonQuery(conn, "DELETE FROM character_checkpoints WHERE character_id = @cid", ("@cid", (int)ch.id));
                    if (ch.unlockedCheckpoints != null)
                    {
                        foreach (var cp in ch.unlockedCheckpoints)
                        {
                            GameDatabase.ExecuteNonQuery(conn,
                                "INSERT OR IGNORE INTO character_checkpoints (character_id, checkpoint_id) VALUES (@cid, @cp)",
                                ("@cid", (int)ch.id), ("@cp", cp));
                        }
                    }

                    tx.Commit();
                    Debug.LogError($"[DB-CHAR] Saved '{ch.name}' lv={ch.level} xp={ch.experience}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DB-CHAR] SaveCharacter error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ═══════════════════════════════════════════════════════════
        // DELETE
        // ═══════════════════════════════════════════════════════════

        public static bool DeleteCharacter(uint characterId)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    // CASCADE handles sub-tables
                    GameDatabase.ExecuteNonQuery(conn, "DELETE FROM characters WHERE id = @id", ("@id", (int)characterId));
                    Debug.LogError($"[DB-CHAR] Deleted character ID: {characterId}");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[DB-CHAR] Delete error: {ex.Message}");
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════
        // PRIVATE HELPERS — read sub-tables
        // ═══════════════════════════════════════════════════════════

        private static SavedCharacter ReadCharacterRow(SqliteDataReader r)
        {
            return new SavedCharacter
            {
                id = (uint)GameDatabase.GetInt(r, "id"),
                accountId = (uint)GameDatabase.GetInt(r, "account_id"),
                name = GameDatabase.GetString(r, "name"),
                className = GameDatabase.GetString(r, "class_name", "Fighter"),
                avatarClass = GameDatabase.GetString(r, "avatar_class"),
                level = (byte)GameDatabase.GetInt(r, "level", 1),
                experience = (uint)GameDatabase.GetInt(r, "experience"),
                gold = (uint)GameDatabase.GetInt(r, "gold", 100),
                skin = (byte)GameDatabase.GetInt(r, "skin"),
                face = (byte)GameDatabase.GetInt(r, "face"),
                faceFeature = (byte)GameDatabase.GetInt(r, "face_feature"),
                hair = (byte)GameDatabase.GetInt(r, "hair"),
                hairColor = (byte)GameDatabase.GetInt(r, "hair_color"),
                zoneId = GameDatabase.GetInt(r, "zone_id"),
                currentZoneName = GameDatabase.GetString(r, "current_zone", "tutorial"),
                position = new Vector3(
                    GameDatabase.GetFloat(r, "position_x"),
                    GameDatabase.GetFloat(r, "position_y"),
                    GameDatabase.GetFloat(r, "position_z")),
                currentHP = (uint)GameDatabase.GetInt(r, "current_hp"),
                currentMana = (uint)GameDatabase.GetInt(r, "current_mana"),
                tpZone = GameDatabase.GetString(r, "tp_zone", ""),
                tpZoneId = GameDatabase.GetInt(r, "tp_zone_id"),
                tpTargetZone = GameDatabase.GetString(r, "tp_target_zone", ""),
                tpPosX = GameDatabase.GetFloat(r, "tp_pos_x"),
                tpPosY = GameDatabase.GetFloat(r, "tp_pos_y"),
                tpPosZ = GameDatabase.GetFloat(r, "tp_pos_z"),
                statStrength = GameDatabase.GetInt(r, "stat_strength"),
                statAgility = GameDatabase.GetInt(r, "stat_agility"),
                statIntellect = GameDatabase.GetInt(r, "stat_intellect"),
                statEndurance = GameDatabase.GetInt(r, "stat_endurance"),
                lastRespecTime = GameDatabase.GetInt(r, "last_respec_time"),
                respecCount = GameDatabase.GetInt(r, "respec_count"),
                pvpWins = GameDatabase.GetInt(r, "pvp_wins"),
                pvpRating = GameDatabase.GetInt(r, "pvp_rating"),
            };
        }

        private static void InsertEquipment(SqliteConnection conn, int charId, string slot, string gcClass, int rarity = -1, int storedLevel = -1)
        {
            GameDatabase.ExecuteNonQuery(conn,
                "INSERT INTO character_equipment (character_id, slot, gc_class, rarity, stored_level) VALUES (@cid, @s, @gc, @r, @sl)",
                ("@cid", charId), ("@s", slot), ("@gc", gcClass ?? ""), ("@r", rarity), ("@sl", storedLevel));
        }

        private static StartingEquipment LoadEquipment(SqliteConnection conn, int charId)
        {
            var equip = new StartingEquipment();
            using (var r = GameDatabase.ExecuteReader(conn,
                "SELECT slot, gc_class, COALESCE(rarity, -1), COALESCE(stored_level, -1) FROM character_equipment WHERE character_id = @cid",
                ("@cid", charId)))
            {
                while (r.Read())
                {
                    string slot = r.GetString(0);
                    string gc = r.GetString(1);
                    int rarity = r.GetInt32(2);
                    int storedLevel = r.GetInt32(3);

                    // ═══════════════════════════════════════════════════════════════
                    // EQUIPMENT ROW VALIDATOR
                    // ═══════════════════════════════════════════════════════════════
                    // A corrupt character_equipment row can produce bytes in the
                    // avatar DFC stream that the client's readInit can't parse,
                    // which trips a fatal assert in the client (null-deref at
                    // EIP 0x006F993E, World='NULL' during login). Validating and
                    // clamping here catches the bad row BEFORE it reaches the
                    // serializer, and logs enough detail to find the culprit
                    // when it happens.
                    //
                    // NOTE: An empty gc_class is NOT corruption — it's the normal
                    // representation of an unequipped slot (characters who don't
                    // have a helmet/shoulders/shield/ring/amulet equipped still
                    // have placeholder rows with gc_class=''). The old loader
                    // passed these through and downstream code checks
                    // IsNullOrEmpty before using them. We preserve that behavior.
                    //
                    // Valid ranges:
                    //   slot         ∈ {weapon,armor,helmet,gloves,boots,shoulders,shield,ring1,ring2,amulet}
                    //   rarity       -1 (default sentinel) or 0..5 (Normal..Mythic)
                    //   stored_level -1 (default sentinel) or 1..120
                    // ═══════════════════════════════════════════════════════════════
                    bool bad = false;
                    string reason = "";
                    switch (slot)
                    {
                        case "weapon":
                        case "armor":
                        case "helmet":
                        case "gloves":
                        case "boots":
                        case "shoulders":
                        case "shield":
                        case "ring1":
                        case "ring2":
                        case "amulet":
                            break;
                        default:
                            bad = true; reason += $"unknown-slot({slot}) "; break;
                    }
                    if (rarity != -1 && (rarity < 0 || rarity > 5))
                    { bad = true; reason += $"rarity-out-of-range({rarity}) "; }
                    if (storedLevel != -1 && (storedLevel < 1 || storedLevel > 120))
                    { bad = true; reason += $"stored_level-out-of-range({storedLevel}) "; }

                    if (bad)
                    {
                        Debug.LogError(
                            $"[EQUIP-VALIDATOR] ⚠️ CORRUPT ROW in character_equipment " +
                            $"char_id={charId} slot='{slot}' gc_class='{gc ?? "NULL"}' " +
                            $"rarity={rarity} stored_level={storedLevel} " +
                            $"reasons={reason.Trim()}");
                        Debug.LogError(
                            $"[EQUIP-VALIDATOR] → clamping row to safe defaults before passing to serializer");
                        // Sanitize in-memory so the serializer gets sane bytes. The DB
                        // row is NOT modified — only the loaded copy — so the original
                        // data is preserved for later inspection.
                        if (rarity != -1 && (rarity < 0 || rarity > 5)) rarity = -1;
                        if (storedLevel != -1 && (storedLevel < 1 || storedLevel > 120)) storedLevel = -1;
                        // Unknown slot: skip entirely — old code populated
                        // slotRarity/slotLevel dicts with the bad slot name but never
                        // assigned any field, so skipping is behaviorally equivalent
                        // and avoids polluting the dicts.
                        switch (slot)
                        {
                            case "weapon":
                            case "armor":
                            case "helmet":
                            case "gloves":
                            case "boots":
                            case "shoulders":
                            case "shield":
                            case "ring1":
                            case "ring2":
                            case "amulet": break;
                            default:
                                Debug.LogError($"[EQUIP-VALIDATOR] → SKIPPING row (unknown slot '{slot}')");
                                continue;
                        }
                    }

                    equip.slotRarity[slot] = rarity;
                    equip.slotLevel[slot] = storedLevel;
                    switch (slot)
                    {
                        case "weapon": equip.weapon = gc; break;
                        case "armor": equip.armor = gc; break;
                        case "helmet": equip.helmet = gc; break;
                        case "gloves": equip.gloves = gc; break;
                        case "boots": equip.boots = gc; break;
                        case "shoulders": equip.shoulders = gc; break;
                        case "shield": equip.shield = gc; break;
                        case "ring1": equip.ring1 = gc; break;
                        case "ring2": equip.ring2 = gc; break;
                        case "amulet": equip.amulet = gc; break;
                    }
                }
            }
            return equip;
        }

        private static List<string> LoadSkillList(SqliteConnection conn, int charId)
        {
            var skills = new List<string>();
            using (var r = GameDatabase.ExecuteReader(conn,
                "SELECT skill_gc_class FROM character_skills WHERE character_id = @cid",
                ("@cid", charId)))
            {
                while (r.Read())
                    skills.Add(r.GetString(0));
            }
            return skills;
        }

        private static List<SkillLevelEntry> LoadSkillLevels(SqliteConnection conn, int charId)
        {
            var levels = new List<SkillLevelEntry>();
            using (var r = GameDatabase.ExecuteReader(conn,
                "SELECT skill_gc_class, level FROM character_skills WHERE character_id = @cid",
                ("@cid", charId)))
            {
                while (r.Read())
                    levels.Add(new SkillLevelEntry { skill = r.GetString(0), level = r.GetInt32(1) });
            }
            return levels;
        }

        private static List<HotbarSlotEntry> LoadHotbarSlots(SqliteConnection conn, int charId)
        {
            var slots = new List<HotbarSlotEntry>();
            using (var r = GameDatabase.ExecuteReader(conn,
                "SELECT skill_gc_class, hotbar_slot FROM character_skills WHERE character_id = @cid AND hotbar_slot >= 0",
                ("@cid", charId)))
            {
                while (r.Read())
                    slots.Add(new HotbarSlotEntry { skill = r.GetString(0), slot = (uint)r.GetInt32(1) });
            }
            return slots;
        }

        private static List<SavedInventoryItem> LoadInventory(SqliteConnection conn, int charId)
        {
            var items = new List<SavedInventoryItem>();
            using (var r = GameDatabase.ExecuteReader(conn,
                "SELECT gc_class, slot_x, slot_y, count, COALESCE(buy_price, 0), COALESCE(rarity, -1), COALESCE(stored_level, -1) FROM character_inventory WHERE character_id = @cid",
                ("@cid", charId)))
            {
                while (r.Read())
                {
                    string gc = r.GetString(0);
                    int sx = r.GetInt32(1);
                    int sy = r.GetInt32(2);
                    int count = r.GetInt32(3);
                    int buyPrice = r.GetInt32(4);
                    int rarity = r.GetInt32(5);
                    int storedLevel = r.GetInt32(6);

                    // ═══════════════════════════════════════════════════════════════
                    // INVENTORY ROW VALIDATOR
                    // ═══════════════════════════════════════════════════════════════
                    // Same rationale as LoadEquipment above: a corrupt row here can
                    // produce bytes that trip the client's fatal-error path during
                    // login inventory restore. Catch and clamp before it reaches
                    // the serializer.
                    //
                    // Valid ranges:
                    //   gc_class     ≠ null, ≠ ""
                    //   slot_x       0..9  (10-column grid)
                    //   slot_y       0..7  (8-row grid)
                    //   count        ≥ 1
                    //   rarity       -1 or 0..5
                    //   stored_level -1 or 1..120
                    // ═══════════════════════════════════════════════════════════════
                    bool bad = false;
                    string reason = "";
                    if (string.IsNullOrEmpty(gc))
                    { bad = true; reason += "empty-gc_class "; }
                    if (sx < 0 || sx > 9)
                    { bad = true; reason += $"slot_x-out-of-range({sx}) "; }
                    if (sy < 0 || sy > 7)
                    { bad = true; reason += $"slot_y-out-of-range({sy}) "; }
                    if (count < 1)
                    { bad = true; reason += $"count-invalid({count}) "; }
                    if (rarity != -1 && (rarity < 0 || rarity > 5))
                    { bad = true; reason += $"rarity-out-of-range({rarity}) "; }
                    if (storedLevel != -1 && (storedLevel < 1 || storedLevel > 120))
                    { bad = true; reason += $"stored_level-out-of-range({storedLevel}) "; }

                    if (bad)
                    {
                        Debug.LogError(
                            $"[INV-VALIDATOR] ⚠️ CORRUPT ROW in character_inventory " +
                            $"char_id={charId} slot=({sx},{sy}) gc_class='{gc ?? "NULL"}' " +
                            $"count={count} rarity={rarity} stored_level={storedLevel} " +
                            $"buy_price={buyPrice} reasons={reason.Trim()}");
                        // Skip rows we can't repair; clamp the rest. DB is not modified.
                        if (string.IsNullOrEmpty(gc) || sx < 0 || sx > 9 || sy < 0 || sy > 7)
                        {
                            Debug.LogError($"[INV-VALIDATOR] → SKIPPING row (unrepairable)");
                            continue;
                        }
                        if (count < 1) count = 1;
                        if (rarity != -1 && (rarity < 0 || rarity > 5)) rarity = -1;
                        if (storedLevel != -1 && (storedLevel < 1 || storedLevel > 120)) storedLevel = -1;
                        Debug.LogError($"[INV-VALIDATOR] → clamped: count={count} rarity={rarity} stored_level={storedLevel}");
                    }

                    items.Add(new SavedInventoryItem
                    {
                        gcClass = gc,
                        x = (byte)sx,
                        y = (byte)sy,
                        count = count,
                        buyPrice = (uint)buyPrice,
                        rarity = rarity,
                        storedLevel = storedLevel
                    });
                }
            }
            return items;
        }

        private static List<SavedQuest> LoadActiveQuests(SqliteConnection conn, int charId)
        {
            var quests = new List<SavedQuest>();
            using (var r = GameDatabase.ExecuteReader(conn,
                "SELECT quest_id, quest_giver_id, accepted_at FROM character_quests WHERE character_id = @cid AND status = 'active'",
                ("@cid", charId)))
            {
                while (r.Read())
                {
                    var q = new SavedQuest
                    {
                        questId = r.GetString(0),
                        questGiverId = r.IsDBNull(1) ? "" : r.GetString(1),
                        acceptedAt = r.IsDBNull(2) ? "" : r.GetString(2),
                        objectives = new List<SavedQuestObjective>()
                    };
                    quests.Add(q);
                }
            }

            // Load objectives for each quest
            foreach (var q in quests)
            {
                using (var r = GameDatabase.ExecuteReader(conn,
                    "SELECT objective_name, type, target, label, required, current FROM quest_objectives WHERE character_id = @cid AND quest_id = @qid",
                    ("@cid", charId), ("@qid", q.questId)))
                {
                    while (r.Read())
                    {
                        q.objectives.Add(new SavedQuestObjective
                        {
                            objectiveName = r.IsDBNull(0) ? "" : r.GetString(0),
                            type = r.IsDBNull(1) ? "" : r.GetString(1),
                            target = r.IsDBNull(2) ? "" : r.GetString(2),
                            label = r.IsDBNull(3) ? "" : r.GetString(3),
                            required = r.GetInt32(4),
                            current = r.GetInt32(5)
                        });
                    }
                }
            }

            return quests;
        }

        private static List<string> LoadCompletedQuests(SqliteConnection conn, int charId)
        {
            var quests = new List<string>();
            using (var r = GameDatabase.ExecuteReader(conn,
                "SELECT quest_id FROM completed_quests WHERE character_id = @cid",
                ("@cid", charId)))
            {
                while (r.Read())
                    quests.Add(r.GetString(0));
            }
            return quests;
        }

        private static List<string> LoadCheckpoints(SqliteConnection conn, int charId)
        {
            var cps = new List<string>();
            using (var r = GameDatabase.ExecuteReader(conn,
                "SELECT checkpoint_id FROM character_checkpoints WHERE character_id = @cid",
                ("@cid", charId)))
            {
                while (r.Read())
                {
                    string cpId = r.GetString(0);
                    // Ensure world.checkpoints. prefix is present (legacy data may lack it)
                    if (!cpId.StartsWith("world.checkpoints.", System.StringComparison.OrdinalIgnoreCase))
                        cpId = "world.checkpoints." + cpId;
                    cps.Add(cpId);
                }
            }
            return cps;
        }

        public static void UpdatePvpRecord(string accountName, bool isWin, int ratingDelta)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(conn, @"
                        UPDATE characters SET
                            pvp_wins   = pvp_wins + @winInc,
                            pvp_rating = MIN(3000, MAX(0, pvp_rating + @rdelta))
                        WHERE id IN (
                            SELECT c.id FROM characters c
                            INNER JOIN accounts a ON c.account_id = a.id
                            WHERE LOWER(a.name) = LOWER(@acct)
                        )",
                        ("@winInc", isWin ? 1 : 0),
                        ("@rdelta", ratingDelta),
                        ("@acct", accountName ?? ""));
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[CharacterRepository.UpdatePvpRecord] {accountName}: {ex.Message}");
            }
        }
    }
}
