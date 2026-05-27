using System;
using UnityEngine;
using DungeonRunners.Utilities;
using DungeonRunners.Data;
using DungeonRunners.Combat;
using DungeonRunners.Managers;
using DungeonRunners.Database;
using DungeonRunners.Core;
using System.Linq;
using System.Collections.Generic;


namespace DungeonRunners.Networking
{
    public class ChatCommandHandler
    {
        private UnityGameServer _server;
        private Dictionary<string, float> _levelCooldown = new Dictionary<string, float>();

        public ChatCommandHandler(UnityGameServer server)
        {
            _server = server;
        }

        private int ParseQty(string command)
        {
            string[] parts = command.Split(' ');
            if (parts.Length >= 2 && int.TryParse(parts[1], out int qty))
                return Math.Max(1, Math.Min(qty, 99));
            return 1;
        }

        public bool HandleChatMessage(RRConnection conn, byte[] data, Action<RRConnection, string> sendMessage)
        {
            if (data == null || data.Length < 1) return false;

            var chatReader = new LEReader(data);
            string message = chatReader.ReadCString();

            if (!message.StartsWith("@")) return false;

            string command = message.Substring(1).Trim().ToLower();

            // @duel is normal gameplay (not admin) — must come before the admin gate.
            if (command == "duel" || command.StartsWith("duel "))
            {
                string duelArgs = command.Length > 4 ? command.Substring(4).Trim() : "";
                return HandleDuelCommand(conn, duelArgs, sendMessage);
            }

            // All other @ commands require admin
            if (!_server.IsPlayerAdmin(conn.LoginName))
            {
                sendMessage(conn, "You do not have permission to use this command.");
                return true;
            }

            if (command.StartsWith("pvp"))
            {
                string args = command.Length > 3 ? command.Substring(3).Trim() : "";
                return HandlePvpCommand(conn, args, sendMessage);
            }

            if (command.StartsWith("group"))
            {
                string args = command.Length > 5 ? command.Substring(5).Trim() : "";
                return HandleGroupCommand(conn, args, sendMessage);
            }

            if (command.StartsWith("g "))
            {
                string args = command.Substring(2).Trim();
                return HandleGroupCommand(conn, args, sendMessage);
            }

            // @kick <name> — shortcut for @group kick <name>
            if (command.StartsWith("kick "))
            {
                string targetName = command.Substring(5).Trim();
                return HandleGroupCommand(conn, "kick " + targetName, sendMessage);
            }

            // @promote <name> — shortcut for @group leader <name>
            if (command.StartsWith("promote "))
            {
                string targetName = command.Substring(8).Trim();
                return HandleGroupCommand(conn, "leader " + targetName, sendMessage);
            }

            // @duelarena <name>         — both into PVPGroupDuelMatch
            // @duelpractice <name>      — both into PVPGroupPracticeMatch (less strict zone)
            // @duelzone <zone> <name>   — both into a specific zone (debug)
            if (command.StartsWith("duelarena "))
            {
                string targetName = command.Substring(10).Trim();
                return HandleDuelArenaProbe(conn, "PVPGroupDuelMatch", targetName, sendMessage);
            }
            if (command.StartsWith("duelpractice "))
            {
                string targetName = command.Substring(13).Trim();
                return HandleDuelArenaProbe(conn, "PVPGroupPracticeMatch", targetName, sendMessage);
            }
            if (command.StartsWith("duelzone "))
            {
                string rest = command.Substring(9).Trim();
                int sp = rest.IndexOf(' ');
                if (sp <= 0) { sendMessage(conn, "[DUELZONE] Usage: @duelzone <zoneName> <playerName>"); return true; }
                string zone = rest.Substring(0, sp).Trim();
                string targetName = rest.Substring(sp + 1).Trim();
                return HandleDuelArenaProbe(conn, zone, targetName, sendMessage);
            }

            if (command.StartsWith("behavior"))
            {
                string args = command.Length > 8 ? command.Substring(8).Trim() : "";
                return HandleBehaviorCommand(conn, args, sendMessage);
            }

            if (command.StartsWith("z "))
            {
                string zoneName = command.Substring(2).Trim();
                Debug.LogError($"[CHAT-CMD] @z zone='{zoneName}'");
                sendMessage(conn, $"[Zone] Changing to: {zoneName}");
                _server.ChatChangeZone(conn, zoneName);
                return true;
            }

            if (command.StartsWith("scroll") || command.StartsWith("portal"))
            {
                int qty = ParseQty(command);
                for (int i = 0; i < qty; i++)
                    GiveConsumableItem(conn, "items.consumables.Consumable_TownPortal", "Town Portal Scroll", sendMessage);
                return true;
            }

            if (command.StartsWith("hppot") || command.StartsWith("hpot"))
            {
                int qty = ParseQty(command);
                for (int i = 0; i < qty; i++)
                    GiveConsumableItem(conn, "items.consumables.Consumable_MajorHealthPotion", "Major Health Potion", sendMessage);
                return true;
            }

            if (command.StartsWith("hp") || command.StartsWith("health"))
            {
                int qty = ParseQty(command);
                for (int i = 0; i < qty; i++)
                    GiveConsumableItem(conn, "potionpal.healthpotion_noob", "Health Bottle", sendMessage);
                return true;
            }

            if (command.StartsWith("mpot") || command.StartsWith("manapot"))
            {
                int qty = ParseQty(command);
                for (int i = 0; i < qty; i++)
                    GiveConsumableItem(conn, "items.consumables.Consumable_MajorManaPotion", "Major Mana Potion", sendMessage);
                return true;
            }

            if (command.StartsWith("mp") || command.StartsWith("mana"))
            {
                int qty = ParseQty(command);
                for (int i = 0; i < qty; i++)
                    GiveConsumableItem(conn, "potionpal.manapotion_noob", "Mana Bottle", sendMessage);
                return true;
            }

            if (command == "poison" || command == "dot")
            {
                Debug.LogError($"[CHAT-CMD] @{command} - Applying poison damage modifier");
                ApplyDamageModifier(conn, sendMessage);
                return true;
            }

            if (command.StartsWith("damage ") || command.StartsWith("dmg "))
            {
                string amountStr = command.StartsWith("damage ") ? command.Substring(7).Trim() : command.Substring(4).Trim();
                if (int.TryParse(amountStr, out int damageAmount) && damageAmount > 0)
                    HandleDamageCommand(conn, damageAmount, sendMessage);
                else
                    sendMessage(conn, "[Error] Usage: @damage <amount>");
                return true;
            }

            if (command.StartsWith("heal "))
            {
                string amountStr = command.Substring(5).Trim();
                if (int.TryParse(amountStr, out int healAmount) && healAmount > 0)
                    HandleHealCommand(conn, healAmount, sendMessage);
                else
                    sendMessage(conn, "[Error] Usage: @heal <amount>");
                return true;
            }

            if (command == "fullheal" || command == "fh")
            {
                HandleFullHealCommand(conn, sendMessage);
                return true;
            }

            // ═══════════════════════════════════════════════════════════════════
            // QUICK SPAWN
            // ═══════════════════════════════════════════════════════════════════

            if (command == "pup") { SpawnMob(conn, "creatures.forestcreatures.warg.basic.pup", sendMessage); return true; }
            if (command == "wolf") { SpawnMob(conn, "creatures.forestcreatures.warg.basic.grunt", sendMessage); return true; }
            if (command == "rat" || command == "ratling") { SpawnMob(conn, "creatures.whiskers.broodling.basic.grunt", sendMessage); return true; }
            if (command == "blade" || command == "blademaster") { SpawnMob(conn, "creatures.whiskers.blademaster.basic.grunt", sendMessage); return true; }
            if (command == "boss" || command == "rattletooth") { SpawnMob(conn, "creatures.whiskers.broodling.basic.champion", sendMessage); return true; }

            // @barrel [count] — spawn 1-10 barrels near player for loot testing
            if (command == "barrel" || command.StartsWith("barrel "))
            {
                int barrelCount = 1;
                if (command.StartsWith("barrel "))
                {
                    string arg = command.Substring(7).Trim();
                    if (int.TryParse(arg, out int bc) && bc >= 1 && bc <= 10)
                        barrelCount = bc;
                }
                string barrelGcType = "terrain.misc.interactives.Breakiable_Barrel_01";
                uint avatarId = _server.GetPlayerAvatarId(conn.LoginName);
                var cp = CombatManager.Instance.GetPlayer(avatarId);
                float baseX = cp != null ? cp.PosX : conn.PlayerPosX;
                float baseY = cp != null ? cp.PosY : conn.PlayerPosY;
                float baseZ = conn.PlayerPosZ;
                int spawned = 0;
                for (int i = 0; i < barrelCount; i++)
                {
                    float offsetX = 50 + (i * 30);
                    string gcType = barrelGcType;
                    var monster = CombatManager.Instance.SpawnMonster(gcType, baseX + offsetX, baseY, baseZ, 0f, GetZoneName(conn));
                    if (monster != null)
                    {
                        _server.SendMonsterToClient(conn, monster);
                        spawned++;
                    }
                }
                sendMessage(conn, $"[Barrel] Spawned {spawned}/{barrelCount} barrels");
                return true;
            }

            // ═══════════════════════════════════════════════════════════════════
            // ADVANCED SPAWN
            // ═══════════════════════════════════════════════════════════════════

            if (command.StartsWith("spawn "))
            {
                string gcType = command.Substring(6).Trim();
                SpawnMob(conn, gcType, sendMessage);
                return true;
            }

            if (command.StartsWith("spawngroup "))
            {
                var sgParts = command.Substring(11).Trim().Split(' ');
                if (sgParts.Length >= 3)
                {
                    string faction = sgParts[0];
                    int count = int.TryParse(sgParts[1], out int c) ? c : 3;
                    string tier = sgParts[2];
                    var monsters = CombatManager.Instance.SpawnFactionGroup(faction, tier, conn.PlayerPosX, conn.PlayerPosY, conn.PlayerPosZ, count, 10f, GetZoneName(conn));
                    foreach (var m in monsters)
                        _server.SendMonsterToClient(conn, m);
                    sendMessage(conn, $"[Spawn] {monsters.Count} {tier} {faction} creatures");
                }
                else
                    sendMessage(conn, "[Usage] @spawngroup <faction> <count> <tier>");
                return true;
            }

            if (command == "creatures" || command == "factions")
            {
                var factions = DatabaseLoader.Creatures.Select(cr => cr.faction).Distinct().Take(10).ToList();
                sendMessage(conn, "[Factions] " + string.Join(", ", factions));
                return true;
            }

            if (command.StartsWith("listmobs "))
            {
                string faction = command.Substring(9).Trim();
                var mobs = DatabaseLoader.GetCreaturesByFaction(faction).Take(8).ToList();
                foreach (var cr in mobs)
                    sendMessage(conn, $"  {cr.gcType}");
                return true;
            }

            if (command == "killall")
            {
                CombatManager.Instance.ClearAll();
                sendMessage(conn, "[Combat] All monsters cleared");
                return true;
            }

            // Path C validation: send a 0x02 (processEntityInit) packet with a readInit body
            // whose Unit::readInit flag has bit 0x02 set, so the client's [Unit+0x2F0] is
            // overwritten by the server-authoritative HPWire. If Path C is correct, monster HP
            // bars snap to server's value with NO comm-error 3 (no Validate runs on opcode 0x02).
            // See memory: project_dr_reborn_hp_sync_path_c_breakthrough.md
            if (command == "pushhp")
            {
                var nearby = CombatManager.Instance.GetMonstersInRange(conn.PlayerPosX, conn.PlayerPosY, 30f);
                int sent = 0;
                int skipped = 0;
                foreach (var m in nearby)
                {
                    if (m == null || !m.IsAlive) { skipped++; continue; }
                    uint hpWire = CombatManager.Instance.GetMonsterCurrentHPWire(m, "ADMIN-PUSHHP");
                    byte[] packet = CombatPackets.BuildMonsterEntityInitHPRefreshPacket(m, hpWire);
                    _server.SendToClient(conn, packet);
                    sent++;
                    Debug.LogError($"[PUSHHP] {m.Name}#{m.EntityId} hp={hpWire / 256f:F2}/{m.MaxHPWire / 256f:F2} bodyLen={packet.Length}");
                }
                sendMessage(conn, $"[PushHP] Sent 0x02 readInit-body refresh to {sent} alive monster(s) (skipped {skipped}). Watch for comm-error 3.");
                return true;
            }

            // Diagnostic harness for the 666 NoHP combat-during-movement desync. Sets
            // CombatManager.MonsterDamageBonusOverride so every monster attack passes this value
            // as the DamageBonus arg into ComputeNativeWeaponDamageRange. Use 'off' (or any
            // negative number) to disable. Pair with the [DMG-INPUT] log line in
            // CreateMonsterNativeWeaponDamageInput to confirm what the server actually used.
            // See WORK\CHANGELOG_HP_SYNC_DAMAGE_BONUS_DIAGNOSTICS_2026-05-25.md.
            if (command == "setdmgbonus" || command.StartsWith("setdmgbonus "))
            {
                string arg = command.Length > 11 ? command.Substring(11).Trim() : "";
                if (string.IsNullOrEmpty(arg))
                {
                    sendMessage(conn, $"[SetDmgBonus] current override = {(CombatManager.MonsterDamageBonusOverride >= 0 ? CombatManager.MonsterDamageBonusOverride.ToString() : "off")}. Usage: @setdmgbonus <int|off>");
                    return true;
                }
                if (string.Equals(arg, "off", StringComparison.OrdinalIgnoreCase))
                {
                    int prev = CombatManager.MonsterDamageBonusOverride;
                    CombatManager.MonsterDamageBonusOverride = -1;
                    Debug.LogError($"[SETBONUS] disabled (was {(prev >= 0 ? prev.ToString() : "off")})");
                    sendMessage(conn, "[SetDmgBonus] override disabled — monster DamageBonus is back to 0");
                    return true;
                }
                if (!int.TryParse(arg, out int newVal))
                {
                    sendMessage(conn, $"[SetDmgBonus] could not parse '{arg}'. Usage: @setdmgbonus <int|off>");
                    return true;
                }
                int prevVal = CombatManager.MonsterDamageBonusOverride;
                CombatManager.MonsterDamageBonusOverride = Math.Max(0, newVal);
                Debug.LogError($"[SETBONUS] {(prevVal >= 0 ? prevVal.ToString() : "off")} -> {CombatManager.MonsterDamageBonusOverride}");
                sendMessage(conn, $"[SetDmgBonus] override = {CombatManager.MonsterDamageBonusOverride} (monster DamageBonus input to ComputeNativeWeaponDamageRange)");
                return true;
            }

            // ═══════════════════════════════════════════════════════════════════
            // ZONE LIST
            // ═══════════════════════════════════════════════════════════════════

            if (command == "zones" || command.StartsWith("zones "))
            {
                string filter = command.Length > 5 ? command.Substring(5).Trim() : "";
                var allZones = _server.GetZoneNames();
                List<string> matching;
                if (string.IsNullOrEmpty(filter))
                    matching = allZones;
                else
                    matching = allZones.Where(zn => zn.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

                if (matching.Count == 0)
                {
                    sendMessage(conn, $"[Zones] No zones match '{filter}'");
                    return true;
                }
                sendMessage(conn, $"[Zones] {matching.Count} zone(s){(string.IsNullOrEmpty(filter) ? "" : $" matching '{filter}'")}:");
                for (int i = 0; i < matching.Count && i < 100; i += 5)
                {
                    var batch = matching.Skip(i).Take(5);
                    sendMessage(conn, "  " + string.Join(", ", batch));
                }
                if (matching.Count > 100)
                    sendMessage(conn, $"  ... and {matching.Count - 100} more. Use @zones <filter>");
                return true;
            }

            // ═══════════════════════════════════════════════════════════════════
            // ITEM SEARCH
            // ═══════════════════════════════════════════════════════════════════

            if (command.StartsWith("items ") || command.StartsWith("search ") || command.StartsWith("find "))
            {
                string search = command.Substring(command.IndexOf(' ') + 1).Trim().ToLower();
                if (search.Length < 2)
                {
                    sendMessage(conn, "[Items] Search term too short (min 2 chars)");
                    return true;
                }

                // Search item_dimensions — these are the REAL spawnable items with correct casing
                var results = new List<string>();
                try
                {
                    using (var db = GameDatabase.GetConnection())
                    using (var dimReader = GameDatabase.ExecuteReader(db,
                        @"SELECT gc_type FROM item_dimensions WHERE LOWER(gc_type) LIKE @s
                          AND gc_type NOT LIKE '%WeaponPAL%'
                          AND gc_type NOT LIKE '%MythicPAL%'
                          AND SUBSTR(gc_type, INSTR(gc_type, 'PAL.') - 1, 1) NOT IN ('0','1','2','3','4','5','6','7','8','9')
                          AND gc_type NOT LIKE '%PAL.Visual'
                          ORDER BY gc_type",
                        ("@s", "%" + search + "%")))
                    {
                        while (dimReader.Read())
                            results.Add(dimReader.GetString(0));
                    }
                }
                catch { }

                // Also search general items (potions, rings, etc.)
                foreach (var kvp in DatabaseLoader.GeneralItemDatabase)
                {
                    var gi = kvp.Value;
                    if ((gi.gcType != null && gi.gcType.ToLower().Contains(search)) ||
                        (gi.Label != null && gi.Label.ToLower().Contains(search)))
                    {
                        if (!results.Contains(gi.gcType))
                            results.Add(gi.gcType);
                    }
                }

                if (results.Count == 0)
                {
                    sendMessage(conn, $"[Items] No items match '{search}'");
                    return true;
                }
                sendMessage(conn, $"[Items] {results.Count} match(es) for '{search}':");
                foreach (var r in results.Take(15))
                {
                    // Show parent.suffix like "1HSwordPAL.Normal014_Fire"
                    string[] parts = r.Split('.');
                    string display = parts.Length >= 2 ? parts[parts.Length - 2] + "." + parts[parts.Length - 1] : r;
                    sendMessage(conn, $"  {display}");
                }
                if (results.Count > 15)
                    sendMessage(conn, $"  ... {results.Count - 15} more. Narrow your search.");
                sendMessage(conn, "  Use @give <shortname> to add to inventory");
                return true;
            }

            // ═══════════════════════════════════════════════════════════════════
            // GIVE ITEM
            // ═══════════════════════════════════════════════════════════════════

            if (command.StartsWith("give "))
            {
                string gcType = command.Substring(5).Trim();
                if (string.IsNullOrEmpty(gcType))
                {
                    sendMessage(conn, "[Give] Usage: @give <gcType>");
                    return true;
                }
                GiveAnyItem(conn, gcType, sendMessage);
                return true;
            }

            // ═══════════════════════════════════════════════════════════════════
            // LEVEL
            // ═══════════════════════════════════════════════════════════════════

            if (command.StartsWith("level ") || command.StartsWith("setlevel "))
            {
                // 3 second cooldown to prevent spam
                float now = UnityEngine.Time.time;
                if (_levelCooldown.TryGetValue(conn.LoginName, out float lastTime) && now - lastTime < 3.0f)
                {
                    sendMessage(conn, "[Level] Please wait before using this again.");
                    return true;
                }
                _levelCooldown[conn.LoginName] = now;
                string numStr = command.Substring(command.IndexOf(' ') + 1).Trim();
                if (!int.TryParse(numStr, out int targetLevel) || targetLevel < 1 || targetLevel > 100)
                {
                    sendMessage(conn, "[Level] Usage: @level <1-100>");
                    return true;
                }
                var savedChar = _server.GetSavedCharacterForConn(conn);
                if (savedChar == null) { sendMessage(conn, "[Level] Error: Character not found"); return true; }

                PlayerState lvlPs = _server.GetPlayerState(conn.ConnId.ToString());
                if (lvlPs == null) { sendMessage(conn, "[Level] Error: PlayerState not found"); return true; }

                int oldLevel = lvlPs.Level;

                // Update server-side level + stats
                savedChar.level = (byte)targetLevel;
                savedChar.experience = 0;
                CharacterRepository.SaveCharacter(savedChar);
                lvlPs.InitializeStats(savedChar.className ?? "Fighter", savedChar.level);
                lvlPs.Experience = 0;

                // Recalculate equipment bonuses at new level + restore HP/mana
                if (conn.Avatar != null)
                    _server.CalculateEquipmentBonuses(conn.ConnId.ToString(), conn.Avatar);
                lvlPs.RestoreToFull();

                // Send XP packets to client so it processes the level-ups and plays effects
                if (targetLevel > oldLevel)
                {
                    for (int lv = oldLevel; lv < targetLevel; lv++)
                    {
                        uint threshold = PlayerState.GetClientThreshold(lv + 1);
                        uint packetXP = threshold * 256 / 5 + 100;
                        _server.SendAdminXPUpdate(conn, packetXP, (uint)lv);
                    }
                    // The DLL's XP hook on the client echoes each admin XP packet back
                    // to the server, contaminating _lastClientXP. Without this reset the
                    // next mob kill would consume the stale admin value and add a giant
                    // chunk of XP on top of the level we just set, causing an extra level.
                    _server.ResetLastClientXP();
                }
                else if (targetLevel < oldLevel)
                {
                    sendMessage(conn, $"[Level] Set to {targetLevel} (was {oldLevel}). Rezone to apply downlevel.");
                    return true;
                }

                // Send HP sync so client updates health bar at new max
                _server.SendAdminHPSync(conn, lvlPs);

                sendMessage(conn, $"[Level] {oldLevel} → {targetLevel}!");
                return true;
            }

            // ═══════════════════════════════════════════════════════════════════
            // GOLD
            // ═══════════════════════════════════════════════════════════════════

            if (command.StartsWith("gold ") || command.StartsWith("addgold "))
            {
                string numStr = command.Substring(command.IndexOf(' ') + 1).Trim();
                if (!int.TryParse(numStr, out int amount)) { sendMessage(conn, "[Gold] Usage: @gold <amount>"); return true; }

                var savedChar = _server.GetSavedCharacterForConn(conn);
                if (savedChar == null) { sendMessage(conn, "[Gold] Error: Character not found"); return true; }

                if (amount < 0 && savedChar.gold < (uint)(-amount))
                    savedChar.gold = 0;
                else
                    savedChar.gold = (uint)(savedChar.gold + amount);
                CharacterRepository.SaveCharacter(savedChar);
                sendMessage(conn, $"[Gold] {(amount >= 0 ? "+" : "")}{amount:N0} -> Total: {savedChar.gold:N0}");
                return true;
            }

            // ═══════════════════════════════════════════════════════════════════
            // XP
            // ═══════════════════════════════════════════════════════════════════

            if (command.StartsWith("xp ") || command.StartsWith("addxp ") || command.StartsWith("exp "))
            {
                string numStr = command.Substring(command.IndexOf(' ') + 1).Trim();
                if (!uint.TryParse(numStr, out uint amount)) { sendMessage(conn, "[XP] Usage: @xp <amount>"); return true; }

                var savedChar = _server.GetSavedCharacterForConn(conn);
                if (savedChar == null) { sendMessage(conn, "[XP] Error: Character not found"); return true; }

                savedChar.experience += amount;
                CharacterRepository.SaveCharacter(savedChar);
                sendMessage(conn, $"[XP] +{amount:N0} -> Total: {savedChar.experience:N0}");
                return true;
            }

            // ═══════════════════════════════════════════════════════════════════
            // STATS
            // ═══════════════════════════════════════════════════════════════════

            if (command == "stats" || command == "info" || command == "me")
            {
                var savedChar = _server.GetSavedCharacterForConn(conn);
                PlayerState ps = _server.GetPlayerState(conn.ConnId.ToString());

                sendMessage(conn, $"=== {conn.LoginName} ===");
                if (savedChar != null)
                {
                    sendMessage(conn, $"  {savedChar.name} | {savedChar.className} Lv{savedChar.level}");
                    sendMessage(conn, $"  Gold: {savedChar.gold:N0} | XP: {savedChar.experience:N0}");
                }
                if (ps != null)
                    sendMessage(conn, $"  HP: {ps.CurrentHPWire / 256}/{ps.MaxHPWire / 256} | Mana: {ps.CurrentManaWire / 256}/{ps.MaxManaWire / 256}");
                sendMessage(conn, $"  Zone: {conn.CurrentZoneName} | Pos: ({conn.PlayerPosX:F0}, {conn.PlayerPosY:F0})");
                return true;
            }

            // ═══════════════════════════════════════════════════════════════════
            // POS
            // ═══════════════════════════════════════════════════════════════════

            if (command == "pos" || command == "loc" || command == "location")
            {
                sendMessage(conn, $"[Pos] Zone: {conn.CurrentZoneName} ({conn.PlayerPosX:F1}, {conn.PlayerPosY:F1})");
                return true;
            }

            // ═══════════════════════════════════════════════════════════════════
            // WHO
            // ═══════════════════════════════════════════════════════════════════

            if (command == "who" || command == "online" || command == "players")
            {
                var connections = _server.GetConnections();
                var online = connections.Values.Where(co => co.IsConnected && !string.IsNullOrEmpty(co.LoginName)).ToList();
                sendMessage(conn, $"[Online] {online.Count} player(s):");
                foreach (var co in online)
                    sendMessage(conn, $"  {co.LoginName} — {co.CurrentZoneName ?? "???"} ({co.PlayerPosX:F0}, {co.PlayerPosY:F0})");
                return true;
            }

            // ═══════════════════════════════════════════════════════════════════
            // TELEPORT TO PLAYER
            // ═══════════════════════════════════════════════════════════════════

            if (command.StartsWith("goto ") || command.StartsWith("tp "))
            {
                string target = command.Substring(command.IndexOf(' ') + 1).Trim();
                var targetConn = _server.FindConnectionByName(target);
                if (targetConn != null && !string.IsNullOrEmpty(targetConn.CurrentZoneName))
                {
                    sendMessage(conn, $"[TP] Teleporting to {targetConn.LoginName} in {targetConn.CurrentZoneName}");
                    _server.ChatChangeZone(conn, targetConn.CurrentZoneName);
                }
                else
                {
                    sendMessage(conn, $"[TP] Changing to zone: {target}");
                    _server.ChatChangeZone(conn, target);
                }
                return true;
            }

            // ═══════════════════════════════════════════════════════════════════
            // ADMIN SHOP — spawn/despawn merchant NPCs
            // ═══════════════════════════════════════════════════════════════════

            if (command == "shop close" || command == "shop off" || command == "store close")
            {
                _server.DespawnAdminShop(conn, sendMessage);
                return true;
            }

            if (command == "shop" || command == "store" || command == "merchant")
            {
                _server.SpawnAdminShop(conn, sendMessage);
                return true;
            }

            // ═══════════════════════════════════════════════════════════════════
            // SERVER CONFIG — @set @get @reload @config
            // ═══════════════════════════════════════════════════════════════════

            if (command.StartsWith("set "))
            {
                string[] setParts = message.Substring(5).Trim().Split(new[] { ' ' }, 2);
                if (setParts.Length < 2)
                {
                    sendMessage(conn, "[Config] Usage: @set <key> <value>");
                    return true;
                }
                string setKey = setParts[0].Trim();
                string setValue = setParts[1].Trim();
                if (!ServerSettings.IsRuntimeMutableKey(setKey))
                {
                    sendMessage(conn, $"[Config] '{setKey}' is native-authoritative and cannot be changed with @set");
                    return true;
                }
                string oldValue = ServerSettings.GetString(setKey, "(not set)");
                if (ServerSettings.Set(setKey, setValue))
                    sendMessage(conn, $"[Config] {setKey}: {oldValue} -> {setValue} (saved to DB)");
                else
                    sendMessage(conn, $"[Config] '{setKey}' was not changed");
                return true;
            }

            if (command.StartsWith("get "))
            {
                string getKey = command.Substring(4).Trim();
                if (!ServerSettings.IsRuntimeMutableKey(getKey))
                {
                    sendMessage(conn, $"[Config] '{getKey}' is native-authoritative and not runtime mutable");
                    return true;
                }
                var all = ServerSettings.GetAll();
                if (all.TryGetValue(getKey, out var entry))
                    sendMessage(conn, $"[Config] {getKey} = {entry.value} (source: {entry.source})");
                else
                    sendMessage(conn, $"[Config] '{getKey}' not set");
                return true;
            }

            if (command == "reload")
            {
                ServerSettings.Reload();
                DRLog.InitFromConfig();  // Re-apply logging config from server.cfg
                sendMessage(conn, "[Config] server.cfg reloaded!");
                return true;
            }

            // ═══════════════════════════════════════════════════════════════════
            // ADMIN — @setfree @setmember @membership (per-player membership)
            // ═══════════════════════════════════════════════════════════════════

            if (command.StartsWith("setfree"))
            {
                // @setfree PlayerName  — or @setfree (self)
                string targetName = command.Length > 8 ? command.Substring(8).Trim() : conn.LoginName;
                if (string.IsNullOrEmpty(targetName)) targetName = conn.LoginName;
                _server.SetPlayerMembershipPublic(targetName, true);
                sendMessage(conn, $"[Membership] {targetName} set to FREE PLAYER. Relog to apply.");
                return true;
            }

            if (command.StartsWith("setmember"))
            {
                // @setmember PlayerName  — or @setmember (self)
                string targetName = command.Length > 10 ? command.Substring(10).Trim() : conn.LoginName;
                if (string.IsNullOrEmpty(targetName)) targetName = conn.LoginName;
                _server.SetPlayerMembershipPublic(targetName, false);
                sendMessage(conn, $"[Membership] {targetName} set to MEMBER. Relog to apply.");
                return true;
            }

            if (command == "membership" || command == "member")
            {
                bool isFree = _server.IsPlayerFreePublic(conn.LoginName);
                string mode = isFree ? "FREE PLAYER" : "MEMBER";
                float xpMult = GCDatabase.Instance.GetKnob("FreePlayerExperienceMult", 0.87f);
                float expMod = GCDatabase.Instance.GetKnob("ExperienceMod", 5.0f);
                sendMessage(conn, $"[Membership] You are: {mode}");
                if (isFree)
                    sendMessage(conn, $"  XP penalty: ×{xpMult} | Shadow items: ON | Item restrictions: ON");
                else
                    sendMessage(conn, $"  Full XP | All items usable | No restrictions");
                sendMessage(conn, $"  Base ExperienceMod: {expMod}");
                return true;
            }

            if (command == "members" || command == "memberlist")
            {
                var members = _server.GetAllMembershipsPublic();
                sendMessage(conn, $"[Membership] {members.Count} players with DB entries:");
                foreach (var m in members)
                    sendMessage(conn, $"  {m.Key} = {(m.Value ? "FREE" : "MEMBER")}");
                return true;
            }

            // ═══════════════════════════════════════════════════════════════════
            // ADMIN — @ban @unban @setadmin @removeadmin
            // ═══════════════════════════════════════════════════════════════════

            if (command.StartsWith("ban "))
            {
                string targetName = command.Substring(4).Trim();
                if (string.IsNullOrEmpty(targetName)) { sendMessage(conn, "Usage: @ban <player>"); return true; }
                _server.SetPlayerBannedPublic(targetName, true);
                sendMessage(conn, $"[Admin] {targetName} has been BANNED. They will be rejected on next login.");
                return true;
            }

            if (command.StartsWith("unban "))
            {
                string targetName = command.Substring(6).Trim();
                if (string.IsNullOrEmpty(targetName)) { sendMessage(conn, "Usage: @unban <player>"); return true; }
                _server.SetPlayerBannedPublic(targetName, false);
                sendMessage(conn, $"[Admin] {targetName} has been UNBANNED.");
                return true;
            }

            if (command.StartsWith("setadmin "))
            {
                string targetName = command.Substring(9).Trim();
                if (string.IsNullOrEmpty(targetName)) { sendMessage(conn, "Usage: @setadmin <player>"); return true; }
                _server.SetPlayerAdminPublic(targetName, true);
                sendMessage(conn, $"[Admin] {targetName} is now an ADMIN. Relog to apply.");
                return true;
            }

            if (command.StartsWith("removeadmin "))
            {
                string targetName = command.Substring(12).Trim();
                if (string.IsNullOrEmpty(targetName)) { sendMessage(conn, "Usage: @removeadmin <player>"); return true; }
                _server.SetPlayerAdminPublic(targetName, false);
                sendMessage(conn, $"[Admin] {targetName} admin removed. Relog to apply.");
                return true;
            }

            if (command == "config" || command == "settings" || command == "cfg")
            {
                var all = ServerSettings.GetAll()
                    .Where(kvp => ServerSettings.IsRuntimeMutableKey(kvp.Key))
                    .ToList();
                int totalSettings = all.Count;
                sendMessage(conn, $"[Config] {totalSettings} runtime settings loaded:");
                int shown = 0;
                foreach (var kvp in all)
                {
                    string src = kvp.Value.source == "db" ? " [DB]" : "";
                    sendMessage(conn, $"  {kvp.Key} = {kvp.Value.value}{src}");
                    shown++;
                    if (shown >= 30)
                    {
                        sendMessage(conn, $"  ... and {totalSettings - 30} more. Use @get <key>");
                        break;
                    }
                }
                return true;
            }

            if (command.StartsWith("unset ") || command.StartsWith("reset "))
            {
                string unsetKey = command.Substring(command.IndexOf(' ') + 1).Trim();
                ServerSettings.Remove(unsetKey);
                if (!ServerSettings.IsRuntimeMutableKey(unsetKey))
                {
                    sendMessage(conn, $"[Config] Removed ignored DB override for native-authoritative '{unsetKey}'");
                    return true;
                }
                string cfgVal = ServerSettings.GetString(unsetKey, "(default)");
                sendMessage(conn, $"[Config] Removed DB override for '{unsetKey}'. Now using: {cfgVal}");
                return true;
            }

            // ═══════════════════════════════════════════════════════════════════
            // ADMIN — @announce @motd @kick @uptime @broadcast
            // ═══════════════════════════════════════════════════════════════════

            if (command.StartsWith("announce ") || command.StartsWith("ann "))
            {
                string msg = message.Substring(message.IndexOf(' ') + 1).Trim();
                string color = ServerSettings.GetString("announceColor", "#FF4444");
                string effect = ServerSettings.GetString("announceEffect", "glow");
                string formatted = UnityGameServer.WrapChatColor($"[ANNOUNCE] {msg}", color, effect);
                foreach (var kvp in _server.GetConnections())
                {
                    var target = kvp.Value;
                    if (target.IsConnected && !string.IsNullOrEmpty(target.LoginName))
                        sendMessage(target, formatted);
                }
                Debug.LogError($"[ADMIN] {conn.LoginName} announced: {msg}");
                return true;
            }

            if (command == "motd")
            {
                string motd = ServerSettings.GetString("motd", "Welcome to Dungeon Runners!");
                string motdColor = ServerSettings.GetString("motdColor", "");
                sendMessage(conn, UnityGameServer.WrapChatColor($"[MOTD] {motd}", motdColor, ""));
                return true;
            }

            if (command.StartsWith("motd "))
            {
                string newMotd = message.Substring(message.IndexOf(' ') + 1).Trim();
                ServerSettings.Set("motd", newMotd);
                sendMessage(conn, $"[MOTD] Updated: {newMotd}");
                return true;
            }

            if (command.StartsWith("kick "))
            {
                string targetName = command.Substring(5).Trim();
                var target = _server.FindConnectionByName(targetName);
                if (target == null)
                {
                    sendMessage(conn, $"[Kick] Player '{targetName}' not found");
                    return true;
                }
                sendMessage(target, "[Server] You have been kicked.");
                sendMessage(conn, $"[Kick] Kicked {targetName}");
                Debug.LogError($"[ADMIN] {conn.LoginName} kicked {targetName}");
                target.Disconnect();
                return true;
            }

            if (command == "uptime")
            {
                var uptime = System.TimeSpan.FromSeconds(UnityEngine.Time.realtimeSinceStartup);
                string uptimeStr = $"{(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s";
                var connections = _server.GetConnections();
                int online = 0;
                foreach (var kvp in connections)
                    if (kvp.Value.IsConnected && !string.IsNullOrEmpty(kvp.Value.LoginName))
                        online++;
                sendMessage(conn, $"[Server] Uptime: {uptimeStr} | Online: {online}/{QueueConnectionBridge.MaxPlayers}");
                return true;
            }

            if (command.StartsWith("broadcast ") || command.StartsWith("bc "))
            {
                string msg = message.Substring(message.IndexOf(' ') + 1).Trim();
                foreach (var kvp in _server.GetConnections())
                {
                    var target = kvp.Value;
                    if (target.IsConnected && !string.IsNullOrEmpty(target.LoginName))
                        sendMessage(target, msg);
                }
                return true;
            }

            if (command.StartsWith("say "))
            {
                // Send as if from "Server" — all players in all zones see it
                string msg = message.Substring(message.IndexOf(' ') + 1).Trim();
                foreach (var kvp in _server.GetConnections())
                {
                    var target = kvp.Value;
                    if (target.IsConnected && !string.IsNullOrEmpty(target.LoginName))
                        sendMessage(target, $"[Server] {msg}");
                }
                return true;
            }

            if (command.StartsWith("color "))
            {
                // @color #FF0000 Hello world — test any color
                string rest = message.Substring(message.IndexOf(' ') + 1).Trim();
                int spaceIdx = rest.IndexOf(' ');
                if (spaceIdx < 0)
                {
                    sendMessage(conn, "[Color] Usage: @color #RRGGBB message text");
                    return true;
                }
                string testColor = rest.Substring(0, spaceIdx).Trim();
                string testMsg = rest.Substring(spaceIdx + 1).Trim();
                sendMessage(conn, UnityGameServer.WrapChatColor(testMsg, testColor, ""));
                return true;
            }

            if (command.StartsWith("glow "))
            {
                // @glow Hello world — test glow effect
                string glowMsg = message.Substring(message.IndexOf(' ') + 1).Trim();
                string glowColor = ServerSettings.GetString("announceColor", "#FF4444");
                sendMessage(conn, UnityGameServer.WrapChatColor(glowMsg, glowColor, "glow"));
                return true;
            }

            if (command == "colors")
            {
                sendMessage(conn, UnityGameServer.WrapChatColor("Red", "#FF0000", ""));
                sendMessage(conn, UnityGameServer.WrapChatColor("Green", "#00FF00", ""));
                sendMessage(conn, UnityGameServer.WrapChatColor("Blue", "#6464FF", ""));
                sendMessage(conn, UnityGameServer.WrapChatColor("Gold", "#FFCC66", ""));
                sendMessage(conn, UnityGameServer.WrapChatColor("Cyan", "#99FFFF", ""));
                sendMessage(conn, UnityGameServer.WrapChatColor("Pink", "#FF66CC", ""));
                sendMessage(conn, UnityGameServer.WrapChatColor("Orange", "#FF6600", ""));
                sendMessage(conn, UnityGameServer.WrapChatColor("Glow Red", "#FF4444", "glow"));
                sendMessage(conn, UnityGameServer.WrapChatColor("Glow Gold", "#FFCC66", "glow"));
                sendMessage(conn, "[Colors] Use @color #RRGGBB text to test any color");
                return true;
            }

            // ═══════════════════════════════════════════════════════════════════
            // HELP
            // ═══════════════════════════════════════════════════════════════════

            if (command == "help" || command == "?")
            {
                sendMessage(conn, "=== NAVIGATION ===");
                sendMessage(conn, "@zones [filter] @z <zone> @tp <player|zone> @pos");
                sendMessage(conn, "=== PLAYER ===");
                sendMessage(conn, "@stats @level <n> @gold <n> @xp <n> @who");
                sendMessage(conn, "=== HEALING ===");
                sendMessage(conn, "@fh @heal <n> @dmg <n> @poison");
                sendMessage(conn, "=== ITEMS ===");
                sendMessage(conn, "@hp [n] @hppot [n] @mp [n] @mpot [n] @scroll [n]");
                sendMessage(conn, "@items <search> @give <gcType>");
                sendMessage(conn, "@shop — spawn admin vendors | @shop close — despawn");
                sendMessage(conn, "=== MONSTERS ===");
                sendMessage(conn, "@pup @wolf @rat @blade @boss @barrel [n]");
                sendMessage(conn, "@spawn <gc> @spawngroup <faction> <n> <tier>");
                sendMessage(conn, "@creatures @listmobs <faction> @killall");
                sendMessage(conn, "=== ZONE ===");
                sendMessage(conn, "@behavior here <mode> @behavior list");
                sendMessage(conn, "=== GROUP ===");
                sendMessage(conn, "@group create|invite|accept|leave|kick|reset|seed");
                sendMessage(conn, "=== CONFIG ===");
                sendMessage(conn, "@config @get <key> @set <key> <val> @unset <key> @reload");
                sendMessage(conn, "=== ADMIN ===");
                sendMessage(conn, "@announce <msg> @motd [msg] @kick <player> @uptime");
                sendMessage(conn, "@broadcast <msg> @say <msg>");
                sendMessage(conn, "@colors @color #RRGGBB <text> @glow <text>");
                sendMessage(conn, "=== MEMBERSHIP ===");
                sendMessage(conn, "@membership @setfree [player] @setmember [player] @members");
                sendMessage(conn, "=== ACCOUNT ===");
                sendMessage(conn, "@ban <player> @unban <player> @setadmin <player> @removeadmin <player>");
                return true;
            }

            return false;
        }

        // ═══════════════════════════════════════════════════════════════════
        // HELPER - Spawn a mob near player
        // ═══════════════════════════════════════════════════════════════════
        private void SpawnMob(RRConnection conn, string gcType, Action<RRConnection, string> sendMessage)
        {
            uint avatarId = _server.GetPlayerAvatarId(conn.LoginName);
            var combatPlayer = CombatManager.Instance.GetPlayer(avatarId);

            float px, py;
            if (combatPlayer != null)
            {
                px = combatPlayer.PosX + 70;
                py = combatPlayer.PosY;
            }
            else
            {
                px = conn.PlayerPosX + 70;
                py = conn.PlayerPosY;
            }
            float pz = conn.PlayerPosZ;

            var monster = CombatManager.Instance.SpawnMonster(gcType, px, py, pz, 0f, GetZoneName(conn));
            if (monster != null)
            {
                _server.SendMonsterToClient(conn, monster);
                sendMessage(conn, $"[Spawn] {monster.Name} HP:{monster.MaxHP} DMG:{monster.BaseDamage}");
            }
            else
            {
                sendMessage(conn, $"[Error] Not found: {gcType}");
            }
        }

        private string GetZoneName(RRConnection conn)
        {
            // Match the EXACT zone naming convention that ZoneSpawnManager uses
            // for natural spawns. Without this:
            //   - admin mob: monster.ZoneName = "dungeon00_level01"
            //   - natural:   monster.ZoneName = "dungeon00_level01_inst<id>"
            //
            // BroadcastMonsterDespawnToZone compares against conn.CurrentZoneName
            // (which is the bare "dungeon00_level01" with no suffix). For natural
            // mobs that mismatch silently bypasses the broadcast, leaving the
            // client's DLL to handle entity cleanup naturally as part of the
            // death animation. For admin mobs the match SUCCEEDS, the entity is
            // yanked mid-animation, and the mob just disappears instead of
            // dying.
            //
            // Using the instance suffix here means CombatManager strips it inside
            // ResolveDungeonCreaturePath (already does — see _inst handling), so
            // dungeon-specific quest tracking still works.
            string baseName = !string.IsNullOrEmpty(conn.CurrentZoneName)
                ? conn.CurrentZoneName
                : conn.CurrentZoneGcType?.Replace("world.", "") ?? "dungeon00_level01";

            // Public zones (town, tutorial, dew valley) don't get instanced.
            // Match UnityGameServer.IsPublicZone logic so we stay consistent.
            string lower = baseName.ToLower();
            bool isPublic = lower.Contains("town") || lower.Contains("tutorial") ||
                            lower.Contains("dew") || lower.Contains("valley");
            if (isPublic) return baseName;

            return $"{baseName}_inst{conn.InstanceId}";
        }

        // ═══════════════════════════════════════════════════════════════════
        // HELPER - Give any item with proper dimensions
        // ═══════════════════════════════════════════════════════════════════
        private void GiveAnyItem(RRConnection conn, string gcType, Action<RRConnection, string> sendMessage)
        {
            if (conn.UnitContainerId == 0)
            {
                sendMessage(conn, "[Give] Error: UnitContainerId not set");
                return;
            }

            // Validate gcType exists in some database before sending to client
            var itemData = DatabaseLoader.FindItem(gcType);
            var genItem = DatabaseLoader.FindGeneralItem(gcType);
            bool validGcType = (itemData != null || genItem != null);

            // Try with items.pal. prefix if not found
            if (!validGcType && !gcType.StartsWith("items."))
            {
                string prefixed = "items.pal." + gcType;
                var prefixedItem = DatabaseLoader.FindItem(prefixed);
                var prefixedGen = DatabaseLoader.FindGeneralItem(prefixed);
                if (prefixedItem != null || prefixedGen != null)
                {
                    gcType = prefixed;
                    itemData = prefixedItem;
                    genItem = prefixedGen;
                    validGcType = true;
                }
            }

            // Smart short name matching using item_dimensions (has correct casing and real items)
            if (!validGcType)
            {
                string search = gcType.ToLower();
                var matches = new List<string>();
                try
                {
                    using (var db = GameDatabase.GetConnection())
                    {
                        // First try exact suffix match (after last dot)
                        using (var dimReader = GameDatabase.ExecuteReader(db,
                            @"SELECT gc_type FROM item_dimensions WHERE 
                              (LOWER(SUBSTR(gc_type, INSTR(gc_type, '.') + 1)) LIKE @s OR LOWER(gc_type) LIKE @s2)
                              AND gc_type NOT LIKE '%WeaponPAL%'
                              AND gc_type NOT LIKE '%MythicPAL%'
                              AND SUBSTR(gc_type, INSTR(gc_type, 'PAL.') - 1, 1) NOT IN ('0','1','2','3','4','5','6','7','8','9')
                              AND gc_type NOT LIKE '%PAL.Visual'",
                            ("@s", "%." + search), ("@s2", "%" + search + "%")))
                        {
                            while (dimReader.Read())
                                matches.Add(dimReader.GetString(0));
                        }
                    }
                }
                catch { }

                // Also check general items
                foreach (var kvp in DatabaseLoader.GeneralItemDatabase)
                {
                    string lower = kvp.Key.ToLower();
                    if (lower.Contains(search) && !matches.Contains(kvp.Key))
                        matches.Add(kvp.Key);
                }

                // Filter: prefer exact suffix match
                var exactSuffix = matches.Where(m => {
                    string suffix = m.Contains(".") ? m.Substring(m.LastIndexOf('.') + 1).ToLower() : m.ToLower();
                    return suffix == search;
                }).ToList();

                var best = exactSuffix.Count > 0 ? exactSuffix : matches;

                if (best.Count == 1)
                {
                    gcType = best[0];
                    itemData = DatabaseLoader.FindItem(gcType);
                    genItem = DatabaseLoader.FindGeneralItem(gcType);
                    validGcType = true;
                    sendMessage(conn, $"[Give] Found: {gcType}");
                }
                else if (best.Count > 1 && best.Count <= 10)
                {
                    sendMessage(conn, $"[Give] {best.Count} matches for '{gcType}':");
                    foreach (var m in best)
                    {
                        string[] mParts = m.Split('.'); string shortName = mParts.Length >= 2 ? mParts[mParts.Length - 2] + "." + mParts[mParts.Length - 1] : m;
                        sendMessage(conn, $"  {shortName}");
                    }
                    return;
                }
                else if (best.Count > 10)
                {
                    sendMessage(conn, $"[Give] {best.Count} matches. Narrow it down.");
                    foreach (var m in best.Take(8))
                    {
                        string[] mParts = m.Split('.'); string shortName = mParts.Length >= 2 ? mParts[mParts.Length - 2] + "." + mParts[mParts.Length - 1] : m;
                        sendMessage(conn, $"  {shortName}");
                    }
                    return;
                }
                else
                {
                    sendMessage(conn, $"[Give] '{gcType}' not found. Use @items <search> first.");
                    return;
                }
            }

            int itemWidth = 1, itemHeight = 1;
            try
            {
                using (var db = GameDatabase.GetConnection())
                using (var dimReader = GameDatabase.ExecuteReader(db,
                    "SELECT gc_type, width, height FROM item_dimensions WHERE LOWER(gc_type) = LOWER(@g)", ("@g", gcType)))
                {
                    if (dimReader.Read())
                    {
                        // Use the EXACT casing from item_dimensions — client requires it
                        gcType = dimReader.GetString(0);
                        itemWidth = dimReader.GetInt32(1);
                        itemHeight = dimReader.GetInt32(2);
                    }
                }
            }
            catch { }

            if (itemData != null)
            {
                if (itemData.inventoryWidth > 0) itemWidth = itemData.inventoryWidth;
                if (itemData.inventoryHeight > 0) itemHeight = itemData.inventoryHeight;
            }
            if (genItem != null)
            {
                if (genItem.InventoryWidth > 0) itemWidth = genItem.InventoryWidth;
                if (genItem.InventoryHeight > 0) itemHeight = genItem.InventoryHeight;
            }

            string connId = conn.ConnId.ToString();
            byte slotX = 0, slotY = 0;
            bool found = false;
            for (byte row = 0; row < 8 && !found; row++)
            {
                for (byte col = 0; col < 10 && !found; col++)
                {
                    if (!_server.IsInventorySlotOccupied(connId, col, row, itemWidth, itemHeight))
                    {
                        slotX = col;
                        slotY = row;
                        found = true;
                    }
                }
            }

            if (!found)
            {
                sendMessage(conn, "[Give] Inventory full!");
                return;
            }

            PlayerState ps = _server.GetPlayerState(connId);
            uint slot = _server.GetNextInventorySlot(connId);

            // Get correct casing and dimensions from item_dimensions
            string gcTypeToSend = gcType;
            try
            {
                using (var db2 = GameDatabase.GetConnection())
                using (var caseReader = GameDatabase.ExecuteReader(db2,
                    "SELECT gc_type FROM item_dimensions WHERE LOWER(gc_type) = LOWER(@g)", ("@g", gcType)))
                {
                    if (caseReader.Read())
                        gcTypeToSend = caseReader.GetString(0);
                }
            }
            catch { }

            // Strip items.pal. prefix for packet — client expects stripped lowercase
            string packetGcType = gcTypeToSend;
            if (packetGcType.StartsWith("items.pal.", StringComparison.OrdinalIgnoreCase))
                packetGcType = packetGcType.Substring(10);
            packetGcType = packetGcType.ToLowerInvariant();

            // Look up modCount from weapons or armor table
            int modSlots = 1;
            try
            {
                using (var db3 = GameDatabase.GetConnection())
                {
                    // Try weapons (stores with items.pal. prefix)
                    string lookupGc = gcType.ToLowerInvariant();
                    if (!lookupGc.StartsWith("items.pal."))
                        lookupGc = "items.pal." + lookupGc;
                    object mc = GameDatabase.ExecuteScalar(db3,
                        "SELECT mod_count FROM weapons WHERE LOWER(gc_type) = @g",
                        ("@g", lookupGc));
                    if (mc == null || mc == DBNull.Value)
                    {
                        // Try without prefix
                        string stripped = lookupGc.StartsWith("items.pal.") ? lookupGc.Substring(10) : lookupGc;
                        mc = GameDatabase.ExecuteScalar(db3,
                            "SELECT mod_count FROM weapons WHERE LOWER(gc_type) = @g",
                            ("@g", stripped));
                    }
                    if (mc == null || mc == DBNull.Value)
                    {
                        // Try armor (both formats)
                        mc = GameDatabase.ExecuteScalar(db3,
                            "SELECT mod_count FROM armor WHERE LOWER(gc_type) = @g",
                            ("@g", lookupGc));
                    }
                    if (mc == null || mc == DBNull.Value)
                    {
                        string stripped = lookupGc.StartsWith("items.pal.") ? lookupGc.Substring(10) : lookupGc;
                        mc = GameDatabase.ExecuteScalar(db3,
                            "SELECT mod_count FROM armor WHERE LOWER(gc_type) = @g",
                            ("@g", stripped));
                    }
                    if (mc != null && mc != DBNull.Value)
                        modSlots = Convert.ToInt32(mc);
                }
            }
            catch { }

            var wr = new LEWriter();
            wr.WriteByte(0x07);
            wr.WriteByte(0x35);
            wr.WriteUInt16(conn.UnitContainerId);
            wr.WriteByte(0x1E);
            wr.WriteByte(0x0B);
            wr.WriteByte(0xFF);
            wr.WriteCString(packetGcType);
            wr.WriteUInt32(slot);
            wr.WriteByte(slotX);
            wr.WriteByte(slotY);
            wr.WriteByte(0x01);           // quantity
            wr.WriteByte(0x37);           // level 55
            for (int mi = 0; mi < modSlots; mi++)
                wr.WriteByte(0x00);       // empty mod slots (dynamic count!)
            wr.WriteByte(0x01);
            wr.WriteByte(0xFF);
            wr.WriteCString("ScaleModPAL.Rare.Mod1");
            wr.WriteByte(0x03);
            wr.WriteByte(0x15);
            wr.WriteUInt32(0x11111111);
            _server.WritePlayerEntitySynch(conn, wr);
            wr.WriteByte(0x06);

            byte[] givePacket = wr.ToArray();
            Debug.LogError($"[GIVE] gcType={packetGcType}, modSlots={modSlots}, slot={slot}, pos=({slotX},{slotY}), pktLen={givePacket.Length}");
            Debug.LogError($"[GIVE] Packet: {BitConverter.ToString(givePacket)}");
            _server.SendToClient(conn, givePacket);

            var newItem = new GCObject { GCClass = gcType, NativeClass = "Item" };
            _server.TrackInventoryItem(connId, slot, newItem, slotX, slotY);
            _server.OccupyInventorySlots(connId, slotX, slotY, itemWidth, itemHeight);
            _server.SetStackCount(connId, slot, 1);

            string itemLabel = genItem?.Label ?? itemData?.name ?? gcType;
            sendMessage(conn, $"[Give] Added {itemLabel} ({itemWidth}x{itemHeight})");
        }

        // ═══════════════════════════════════════════════════════════════════
        // HELPER - Damage / Heal / FullHeal
        // ═══════════════════════════════════════════════════════════════════
        private void HandleDamageCommand(RRConnection conn, int damageAmount, Action<RRConnection, string> sendMessage)
        {
            string connId = conn.ConnId.ToString();
            PlayerState playerState = _server.GetPlayerState(connId);
            uint maxHP = playerState.MaxHPWire;
            uint damageWire = (uint)(damageAmount * 256);
            playerState.TakeDamage(damageWire);
            uint newHP = playerState.CurrentHPWire;
            sendMessage(conn, $"[Damage] Took {damageAmount} damage! HP: {newHP / 256} / {maxHP / 256}");
        }

        private void HandleHealCommand(RRConnection conn, int healAmount, Action<RRConnection, string> sendMessage)
        {
            string connId = conn.ConnId.ToString();
            PlayerState playerState = _server.GetPlayerState(connId);
            uint maxHP = playerState.MaxHPWire;
            uint healWire = (uint)(healAmount * 256);
            playerState.Heal(healWire);
            uint newHP = playerState.CurrentHPWire;
            sendMessage(conn, $"[Heal] Healed {healAmount}! HP: {newHP / 256} / {maxHP / 256}");
        }

        private void HandleFullHealCommand(RRConnection conn, Action<RRConnection, string> sendMessage)
        {
            string connId = conn.ConnId.ToString();
            PlayerState playerState = _server.GetPlayerState(connId);
            playerState.RestoreToFull();
            uint maxHP = playerState.MaxHPWire;
            sendMessage(conn, $"[FullHeal] HP restored to full: {maxHP / 256} / {maxHP / 256}");
        }

        // ═══════════════════════════════════════════════════════════════════
        // HELPER - Give consumable (stackable)
        // ═══════════════════════════════════════════════════════════════════
        private void GiveConsumableItem(RRConnection conn, string gcClass, string itemName, Action<RRConnection, string> sendMessage)
        {
            if (conn.UnitContainerId == 0)
            {
                sendMessage(conn, "[Error] UnitContainerId not set");
                return;
            }

            string connId = conn.ConnId.ToString();
            PlayerState playerState = _server.GetPlayerState(connId);

            var existing = _server.FindInventoryItemByGCClass(connId, gcClass);
            if (existing != null)
            {
                uint oldSlot = existing.Value.slot;
                byte oldX = existing.Value.x;
                byte oldY = existing.Value.y;
                int oldCount = _server.GetStackCount(connId, oldSlot);
                int newCount = Math.Min(oldCount + 1, 99);

                var writer = new LEWriter();
                writer.WriteByte(0x07);
                writer.WriteByte(0x35);
                writer.WriteUInt16(conn.UnitContainerId);
                writer.WriteByte(0x1F);
                writer.WriteUInt32(oldSlot);
                _server.WritePlayerEntitySynch(conn, writer);

                uint newSlot = _server.GetNextInventorySlot(connId);
                writer.WriteByte(0x35);
                writer.WriteUInt16(conn.UnitContainerId);
                writer.WriteByte(0x1E);
                writer.WriteByte(0x0B);
                writer.WriteByte(0xFF);
                writer.WriteCString(gcClass);
                writer.WriteUInt32(newSlot);
                writer.WriteByte(oldX);
                writer.WriteByte(oldY);
                writer.WriteByte((byte)newCount);
                writer.WriteByte(0x01);
                writer.WriteByte(0x00);
                writer.WriteByte(0x00);
                _server.WritePlayerEntitySynch(conn, writer);

                writer.WriteByte(0x06);
                _server.SendToClient(conn, writer.ToArray());

                _server.RemoveInventoryItemBySlot(connId, oldSlot);
                var item = new GCObject { GCClass = gcClass, NativeClass = "Item" };
                _server.TrackInventoryItem(connId, newSlot, item, oldX, oldY);
                _server.SetStackCount(connId, newSlot, newCount);

                sendMessage(conn, $"[Item] {itemName} x{newCount}");
                return;
            }

            byte slotX = 0, slotY = 0;
            bool found = false;
            for (byte row = 0; row < 8 && !found; row++)
            {
                for (byte col = 0; col < 10 && !found; col++)
                {
                    if (!_server.IsInventorySlotOccupied(connId, col, row, 1, 1))
                    {
                        slotX = col;
                        slotY = row;
                        found = true;
                    }
                }
            }

            if (!found)
            {
                sendMessage(conn, "[Error] Inventory full!");
                return;
            }

            uint equipSlot = _server.GetNextInventorySlot(connId);

            var wr = new LEWriter();
            wr.WriteByte(0x07);
            wr.WriteByte(0x35);
            wr.WriteUInt16(conn.UnitContainerId);
            wr.WriteByte(0x1E);
            wr.WriteByte(0x0B);
            wr.WriteByte(0xFF);
            wr.WriteCString(gcClass);
            wr.WriteUInt32(equipSlot);
            wr.WriteByte(slotX);
            wr.WriteByte(slotY);
            wr.WriteByte(0x01);
            wr.WriteByte(0x01);
            wr.WriteByte(0x00);
            wr.WriteByte(0x00);
            _server.WritePlayerEntitySynch(conn, wr);
            wr.WriteByte(0x06);

            _server.SendToClient(conn, wr.ToArray());

            var newItem = new GCObject { GCClass = gcClass, NativeClass = "Item" };
            _server.TrackInventoryItem(connId, equipSlot, newItem, slotX, slotY);
            _server.OccupyInventorySlots(connId, slotX, slotY, 1, 1);
            _server.SetStackCount(connId, equipSlot, 1);

            sendMessage(conn, $"[Item] Added {itemName}!");
        }

        // ═══════════════════════════════════════════════════════════════════
        // PVP COMMANDS — testing harness for PVPMatchManager
        private bool HandlePvpCommand(RRConnection conn, string args, Action<RRConnection, string> sendMessage)
        {
            args = (args ?? "").Trim();
            if (args.Length == 0)
            {
                sendMessage(conn, "[PVP] Usage: @pvp queue <duel|practice|dm|dmunrated> | cancel | status | leave");
                return true;
            }

            string[] parts = args.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
            string sub = parts[0].ToLowerInvariant();

            if (sub == "queue" || sub == "q")
            {
                if (parts.Length < 2)
                {
                    sendMessage(conn, "[PVP] Specify archetype: duel | practice | dm | dmunrated");
                    return true;
                }
                string atypeStr = parts[1].Trim().ToLowerInvariant();
                Managers.PVPMatchManager.Archetype atype;
                switch (atypeStr)
                {
                    case "duel": atype = Managers.PVPMatchManager.Archetype.GroupDuelMatch; break;
                    case "practice": atype = Managers.PVPMatchManager.Archetype.GroupPracticeMatch; break;
                    case "dm": atype = Managers.PVPMatchManager.Archetype.GroupDeathMatch; break;
                    case "dmunrated":
                    case "dmcasual": atype = Managers.PVPMatchManager.Archetype.GroupDeathMatchUnrated; break;
                    default:
                        sendMessage(conn, $"[PVP] Unknown archetype '{atypeStr}'. Use: duel | practice | dm | dmunrated");
                        return true;
                }

                int rating = 1500;
                try
                {
                    var ch = Database.CharacterRepository.GetCharacterByName(conn.LoginName, conn.LoginName);
                    if (ch != null && ch.pvpRating > 0) rating = ch.pvpRating;
                }
                catch { }

                bool ok = Managers.PVPMatchManager.Instance.EnqueuePlayer(conn.LoginName, conn.CharSqlId, atype, rating);
                sendMessage(conn, ok
                    ? $"[PVP] Queued for {atype} (rating {rating}). Need {Managers.PVPMatchManager.RequiredPlayers(atype)} total."
                    : "[PVP] Could not queue (already queued or in match).");
                return true;
            }

            if (sub == "cancel" || sub == "c")
            {
                bool removed = Managers.PVPMatchManager.Instance.DequeuePlayer(conn.LoginName);
                sendMessage(conn, removed ? "[PVP] Removed from queue." : "[PVP] You weren't queued.");
                return true;
            }

            if (sub == "leave")
            {
                Managers.PVPMatchManager.Instance.DequeuePlayer(conn.LoginName);
                var match = Managers.PVPMatchManager.Instance.HandleDisconnect(conn.LoginName);
                sendMessage(conn, match != null
                    ? $"[PVP] Forfeited match {match.MatchId}."
                    : "[PVP] Not in a match.");
                return true;
            }

            if (sub == "status" || sub == "s")
            {
                var state = Managers.PVPMatchManager.Instance.GetState(conn.LoginName);
                var m = Managers.PVPMatchManager.Instance.GetMatchForPlayer(conn.LoginName);
                sendMessage(conn, $"[PVP] State: {state}. " +
                    (m != null ? $"Match {m.MatchId} ({m.Archetype}) zone={m.ZoneName} players={string.Join(",", m.ParticipantLogins)}"
                               : $"Total queued: {Managers.PVPMatchManager.Instance.TotalQueued}, " +
                                 $"active matches: {Managers.PVPMatchManager.Instance.TotalActiveMatches}"));
                return true;
            }

            sendMessage(conn, $"[PVP] Unknown subcommand '{sub}'. Try: queue | cancel | status | leave");
            return true;
        }

        // ════════════════════════════════════════════════════════════════
        // @duel command — chat-driven 1v1 duel for admins.
        //   @duel <name>          — challenge a player
        //   @duel accept (or a)   — accept incoming challenge
        //   @duel decline (or d)  — decline incoming challenge
        //   @duel status (or s)   — show current duel state
        // The client also has its own opcode-based path (0x2D/0x2E/0x2F)
        // via right-click → Duel; @duel is a deterministic fallback.
        // ════════════════════════════════════════════════════════════════
        private bool HandleDuelCommand(RRConnection conn, string args, Action<RRConnection, string> sendMessage)
        {
            args = (args ?? "").Trim();
            if (args.Length == 0)
            {
                sendMessage(conn, "[DUEL] Usage: @duel <playername> | accept | decline | status");
                return true;
            }

            string sub = args.ToLowerInvariant();

            if (sub == "accept" || sub == "a")
            {
                _server.AcceptDuel(conn);
                sendMessage(conn, "[DUEL] Accept sent.");
                return true;
            }

            if (sub == "decline" || sub == "d")
            {
                bool had = _server.DeclineDuel(conn);
                sendMessage(conn, had ? "[DUEL] Declined." : "[DUEL] No pending duel to decline.");
                return true;
            }

            if (sub == "status" || sub == "s")
            {
                sendMessage(conn, $"[DUEL] {_server.GetDuelStatusFor(conn.LoginName)}");
                return true;
            }

            // Otherwise treat the args as a target player name.
            string targetName = args;
            var targetConn = _server.FindConnectionByName(targetName);
            if (targetConn == null)
            {
                sendMessage(conn, $"[DUEL] Player '{targetName}' not online.");
                return true;
            }
            string err = _server.IssueDuelChallenge(conn, targetConn);
            sendMessage(conn, err == null
                ? $"[DUEL] Challenge sent to {targetName}. They have 30s to accept."
                : $"[DUEL] Cannot challenge {targetName}: {err}");
            return true;
        }

        // ════════════════════════════════════════════════════════════════
        // @duelarena <name> — Option-B PROBE (admin only)
        // Forces both players into the SAME instance of PVPGroupDuelMatch
        // (by temporarily putting them in a group together). Tests whether
        // same-instance presence is enough for the client to allow PvP hits,
        // or whether we still need the 0x4E processChangedPVPStatus payload.
        // ════════════════════════════════════════════════════════════════
        private bool HandleDuelArenaProbe(RRConnection conn, string zoneName, string targetName, Action<RRConnection, string> sendMessage)
        {
            if (string.IsNullOrEmpty(targetName))
            {
                sendMessage(conn, $"[DUELARENA] Usage: @duelarena <playername>  (zone={zoneName})");
                return true;
            }
            var targetConn = _server.FindConnectionByName(targetName);
            if (targetConn == null || targetConn == conn)
            {
                sendMessage(conn, $"[DUELARENA] Target '{targetName}' not online (or same as you).");
                return true;
            }

            // Form a temporary group: challenger creates, invites target, target auto-accepts.
            // This makes AssignInstanceId give them the same instance ID inside the arena zone.
            GroupManager.Instance.LeaveGroup(conn.ConnId);
            GroupManager.Instance.LeaveGroup(targetConn.ConnId);

            var g = GroupManager.Instance.CreateGroup(conn.ConnId, conn.LoginName, conn.LoginName);
            bool invited = GroupManager.Instance.InvitePlayer(conn.ConnId, targetConn.ConnId);
            if (!invited)
            {
                sendMessage(conn, "[DUELARENA] Failed to invite target.");
                return true;
            }
            var joined = GroupManager.Instance.AcceptInvite(targetConn.ConnId, targetConn.LoginName, targetConn.LoginName);
            if (joined == null)
            {
                sendMessage(conn, "[DUELARENA] Target failed to join group.");
                return true;
            }

            Debug.LogError($"[DUELARENA] Probe: temp group {g.GroupId} formed: {conn.LoginName} + {targetConn.LoginName} → zone {zoneName}");
            sendMessage(conn, $"[DUELARENA] Group {g.GroupId} formed with {targetName}. Teleporting both to {zoneName}…");

            _server.ChatChangeZone(conn, zoneName);
            _server.ChatChangeZone(targetConn, zoneName);
            Debug.LogError($"[DUELARENA] Both teleported to {zoneName}. Watch for: same-instance, sync errors, and whether you can damage each other.");
            return true;
        }

        // GROUP COMMANDS
        // ═══════════════════════════════════════════════════════════════════
        private bool HandleGroupCommand(RRConnection conn, string args, Action<RRConnection, string> sendMessage)
        {
            if (string.IsNullOrEmpty(args))
            {
                var group = GroupManager.Instance.GetGroupForConn(conn.ConnId);
                if (group != null)
                {
                    string members = string.Join(", ", group.Members.Select(m =>
                        (m.ConnId == group.LeaderConnId ? "*" : "") + m.Name +
                        (m.IsOnline ? "" : " (offline)") +
                        (!string.IsNullOrEmpty(m.CurrentZoneName) ? $" [{m.CurrentZoneName}]" : "")));
                    sendMessage(conn, $"[Group {group.GroupId}] {members} | Seed: 0x{group.InstanceSeed:X8}");
                }
                else
                {
                    sendMessage(conn, "[Group] Not in a group. Use @group create");
                }
                return true;
            }

            string[] parts = args.Split(' ');
            string sub = parts[0].ToLower();

            switch (sub)
            {
                case "create":
                    {
                        var group = GroupManager.Instance.CreateGroup(conn.ConnId, conn.LoginName, conn.LoginName);
                        sendMessage(conn, $"[Group] Created group {group.GroupId}. You are leader. Use @group invite <name> to invite.");
                        return true;
                    }

                case "invite":
                    {
                        if (parts.Length < 2) { sendMessage(conn, "[Group] Usage: @group invite <name>"); return true; }
                        string targetName = parts[1];
                        var target = _server.FindConnectionByName(targetName);
                        if (target == null) { sendMessage(conn, $"[Group] Player '{targetName}' not found"); return true; }
                        if (!GroupManager.Instance.IsInGroup(conn.ConnId))
                        {
                            GroupManager.Instance.CreateGroup(conn.ConnId, conn.LoginName, conn.LoginName);
                            sendMessage(conn, $"[Group] Created group. You are leader.");
                        }
                        if (GroupManager.Instance.InvitePlayer(conn.ConnId, target.ConnId))
                        {
                            // Send REAL processInvitation (0x32) to target — shows GroupInviteDialog
                            // Binary: processInvitation @ 0x5F8210 reads:
                            //   uint32 inviteId, uint32 groupId, string name\0, byte flags
                            var group = GroupManager.Instance.GetGroupForConn(conn.ConnId);
                            uint inviterCharId = _server.GetPlayerAvatarId(conn.LoginName);
                            string inviterCharName = conn.LoginName;
                            var selChar = _server.GetSelectedCharacter(conn.LoginName);
                            if (selChar != null) inviterCharName = selChar.Name ?? inviterCharName;

                            byte[] invitePacket = GroupPackets.BuildProcessInvitation(
                                inviterCharId, group.GroupId, inviterCharName, 0x00);
                            _server.SendToClient(target, invitePacket);
                            sendMessage(conn, $"[Group] Invite sent to {targetName}");
                            Debug.LogError($"[GROUP] @invite: Sent processInvitation(0x32) to {target.LoginName}: inviteId=0x{inviterCharId:X8} group={group.GroupId} name='{inviterCharName}'");
                        }
                        else
                            sendMessage(conn, $"[Group] Cannot invite {targetName}");
                        return true;
                    }

                case "accept":
                    {
                        var group = GroupManager.Instance.AcceptInvite(conn.ConnId, conn.LoginName, conn.LoginName);
                        if (group != null)
                        {
                            sendMessage(conn, $"[Group] Joined group {group.GroupId}!");
                            _server.SendGroupConnectedToAll(group);
                        }
                        else
                            sendMessage(conn, "[Group] No pending invite");
                        return true;
                    }

                case "decline":
                    GroupManager.Instance.DeclineInvite(conn.ConnId);
                    sendMessage(conn, "[Group] Invite declined");
                    return true;

                case "leave":
                    {
                        var group = GroupManager.Instance.GetGroupForConn(conn.ConnId);
                        if (group != null)
                        {
                            foreach (var m in group.Members)
                            {
                                if (m.ConnId == conn.ConnId) continue;
                                var memberConn = _server.FindConnectionById(m.ConnId);
                                if (memberConn != null)
                                    _server.SendSystemMessage(memberConn, $"[Group] {conn.LoginName} left the group");
                            }
                        }
                        GroupManager.Instance.LeaveGroup(conn.ConnId);
                        sendMessage(conn, "[Group] Left group");
                        return true;
                    }

                case "kick":
                    {
                        if (parts.Length < 2) { sendMessage(conn, "[Group] Usage: @group kick <name>"); return true; }
                        var target = _server.FindConnectionByName(parts[1]);
                        if (target == null) { sendMessage(conn, $"[Group] Player '{parts[1]}' not found"); return true; }
                        var group = GroupManager.Instance.GetGroupForConn(conn.ConnId);
                        if (group == null || group.LeaderConnId != conn.ConnId)
                        { sendMessage(conn, "[Group] You are not the leader"); return true; }
                        if (target.ConnId == conn.ConnId)
                        { sendMessage(conn, "[Group] Can't kick yourself"); return true; }
                        // Send processRemoveUser (0x43) to all members
                        uint kickCharId = _server.GetCharSqlIdPublic(target);
                        byte[] kickPacket = GroupPackets.BuildProcessRemoveUser(group.GroupId, kickCharId);
                        foreach (var m in group.Members)
                        {
                            var mc = _server.FindConnectionById(m.ConnId);
                            if (mc != null) _server.SendToClient(mc, kickPacket);
                        }
                        GroupManager.Instance.LeaveGroup(target.ConnId);
                        target.GroupConnectedSent = false;
                        // Solo 0x35 to kicked player — clears their group UI
                        byte[] kickSolo = GroupPackets.BuildProcessUserChangedGroup(
                            1, target.CurrentZoneId, 0xFF, 0, 0, 0, 0, new GroupMemberInfo[0]);
                        _server.SendCompressedPublic(target, 0x01, 0x0F, kickSolo);
                        // Update remaining members
                        if (group.Members.Count == 1)
                        {
                            var lastConn = _server.FindConnectionById(group.Members[0].ConnId);
                            if (lastConn != null)
                            {
                                byte[] soloState = GroupPackets.BuildProcessUserChangedGroup(
                                    1, lastConn.CurrentZoneId, 0xFF, 0, 0, 0, 0, new GroupMemberInfo[0]);
                                _server.SendCompressedPublic(lastConn, 0x01, 0x0F, soloState);
                                lastConn.GroupConnectedSent = false;
                            }
                            GroupManager.Instance.LeaveGroup(group.Members[0].ConnId);
                        }
                        else
                        {
                            _server.SendGroupConnectedToAll(group);
                        }
                        SocialManager.Instance.PushWhoListToAll(_server.SendSocialViaAuthPublic);
                        _server.SendSystemMessage(target, "[Group] You were removed from the group");
                        sendMessage(conn, $"[Group] Kicked {parts[1]}");
                        Debug.LogError($"[GROUP] @kick: Kicked {parts[1]} (charSqlId=0x{kickCharId:X8}) from group {group.GroupId}");
                        return true;
                    }

                case "leader":
                case "promote":
                    {
                        if (parts.Length < 2) { sendMessage(conn, "[Group] Usage: @group leader <n>"); return true; }
                        var target = _server.FindConnectionByName(parts[1]);
                        if (target == null) { sendMessage(conn, $"[Group] Player '{parts[1]}' not found"); return true; }
                        var group = GroupManager.Instance.GetGroupForConn(conn.ConnId);
                        if (group == null || group.LeaderConnId != conn.ConnId)
                        { sendMessage(conn, "[Group] You are not the leader"); return true; }
                        if (target.ConnId == conn.ConnId)
                        { sendMessage(conn, "[Group] You are already the leader"); return true; }
                        uint newLeaderId = _server.GetCharSqlIdPublic(target);
                        GroupManager.Instance.SetLeader(conn.ConnId, target.ConnId);
                        // Reset GroupConnectedSent so everyone gets fresh 0x30 with new GC+0xB0
                        foreach (var m in group.Members)
                        {
                            var mc = _server.FindConnectionById(m.ConnId);
                            if (mc != null)
                                mc.GroupConnectedSent = false;
                        }
                        _server.SendGroupConnectedToAll(group);
                        sendMessage(conn, $"[Group] {parts[1]} is now leader");
                        Debug.LogError($"[GROUP] @leader: Promoted {parts[1]} (charSqlId=0x{newLeaderId:X8}) in group {group.GroupId}");
                        return true;
                    }

                case "reset":
                    GroupManager.Instance.ResetInstances(conn.ConnId);
                    sendMessage(conn, "[Group] Dungeon instances reset. Re-enter to get new layout.");
                    return true;

                case "difficulty":
                case "diff":
                    {
                        if (parts.Length < 2 || !byte.TryParse(parts[1], out byte diff))
                        { sendMessage(conn, "[Group] Usage: @group difficulty <0-4>"); return true; }
                        GroupManager.Instance.SetMonsterDifficulty(conn.ConnId, diff);
                        sendMessage(conn, $"[Group] Monster difficulty set to {diff}");
                        return true;
                    }

                case "seed":
                    {
                        uint seed = GroupManager.Instance.GetGroupSeed(conn.ConnId);
                        sendMessage(conn, $"[Group] Instance seed: 0x{seed:X8}");
                        return true;
                    }

                default:
                    sendMessage(conn, "[Group] Commands: create, invite, accept, decline, leave, kick, leader, reset, difficulty, seed");
                    return true;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // @behavior — zone mob behavior via SQLite
        // ═══════════════════════════════════════════════════════════════════
        private bool HandleBehaviorCommand(RRConnection conn, string args, Action<RRConnection, string> sendMessage)
        {
            if (string.IsNullOrEmpty(args) || args == "modes")
            {
                sendMessage(conn, "[Behavior] Modes: guard | wander | dungeon_specific");
                sendMessage(conn, "[Behavior] Usage: @behavior <zone> <mode>");
                sendMessage(conn, "[Behavior] Usage: @behavior <zone> — show current");
                sendMessage(conn, "[Behavior] Usage: @behavior here <mode> — current zone");
                sendMessage(conn, "[Behavior] Usage: @behavior list — show all overrides");
                sendMessage(conn, "[Behavior] Usage: @behavior clearall — remove all, use cfg default");
                sendMessage(conn, "[Behavior] Usage: @behavior remove <zone> — remove one override");
                return true;
            }

            if (args == "list")
            {
                try
                {
                    using (var db = GameDatabase.GetConnection())
                    using (var dbReader = GameDatabase.ExecuteReader(db,
                        "SELECT zone_name, behavior_mode, enabled FROM zone_behaviors ORDER BY zone_name LIMIT 20"))
                    {
                        int count = 0;
                        while (dbReader.Read())
                        {
                            string zn = dbReader.GetString(0);
                            string mode = dbReader.GetString(1);
                            int enabled = dbReader.GetInt32(2);
                            string status = enabled == 1 ? "" : " [DISABLED]";
                            sendMessage(conn, $"  {zn} -> {mode}{status}");
                            count++;
                        }
                        int total = (int)(long)GameDatabase.ExecuteScalar(db,
                            "SELECT COUNT(*) FROM zone_behaviors");
                        sendMessage(conn, $"[Behavior] Showing {count} of {total}.");
                    }
                }
                catch (Exception ex)
                {
                    sendMessage(conn, $"[Behavior] Error: {ex.Message}");
                }
                return true;
            }

            if (args == "clearall" || args.StartsWith("remove ") || args.StartsWith("delete "))
            {
                sendMessage(conn, "[Behavior] Runtime behavior mutation is disabled.");
                return true;
            }

            if (args == "clearall")
            {
                try
                {
                    using (var db = GameDatabase.GetConnection())
                    {
                        GameDatabase.ExecuteNonQuery(db, "DELETE FROM zone_behaviors");
                    }
                    string def = ServerSettings.GetString("defaultBehavior", "wander");
                    sendMessage(conn, $"[Behavior] Cleared ALL zone overrides. All zones now use cfg default: {def}");
                    CombatManager.Instance.ClearAll();
                }
                catch (Exception ex)
                {
                    sendMessage(conn, $"[Behavior] Error: {ex.Message}");
                }
                return true;
            }

            if (args.StartsWith("remove ") || args.StartsWith("delete "))
            {
                string removeZone = args.Substring(args.IndexOf(' ') + 1).Trim();
                if (removeZone == "here")
                    removeZone = conn.CurrentZoneName ?? "";
                try
                {
                    using (var db = GameDatabase.GetConnection())
                    {
                        GameDatabase.ExecuteNonQuery(db,
                            "DELETE FROM zone_behaviors WHERE zone_name = @z",
                            ("@z", removeZone));
                    }
                    string def = ServerSettings.GetString("defaultBehavior", "wander");
                    sendMessage(conn, $"[Behavior] Removed override for '{removeZone}'. Now uses cfg default: {def}");
                }
                catch (Exception ex)
                {
                    sendMessage(conn, $"[Behavior] Error: {ex.Message}");
                }
                return true;
            }

            string[] bparts = args.Split(' ');
            string zoneName = bparts[0];

            if (zoneName == "here")
                zoneName = conn.CurrentZoneName ?? "";

            if (string.IsNullOrEmpty(zoneName))
            {
                sendMessage(conn, "[Behavior] No zone specified and not in a zone");
                return true;
            }

            if (bparts.Length == 1)
            {
                try
                {
                    using (var db = GameDatabase.GetConnection())
                    using (var dbReader = GameDatabase.ExecuteReader(db,
                        "SELECT behavior_mode, enabled FROM zone_behaviors WHERE zone_name = @z",
                        ("@z", zoneName)))
                    {
                        if (dbReader.Read())
                        {
                            string mode = dbReader.GetString(0);
                            int enabled = dbReader.GetInt32(1);
                            sendMessage(conn, $"[Behavior] {zoneName} -> {mode} (enabled={enabled})");
                        }
                        else
                            sendMessage(conn, $"[Behavior] {zoneName} not found in zone_behaviors");
                    }
                }
                catch (Exception ex)
                {
                    sendMessage(conn, $"[Behavior] Error: {ex.Message}");
                }
                return true;
            }

            if (bparts.Length > 1)
            {
                sendMessage(conn, "[Behavior] Runtime behavior mutation is disabled.");
                return true;
            }

            string newMode = bparts[1].ToLower();
            if (newMode != "guard" && newMode != "wander" && newMode != "dungeon_specific")
            {
                sendMessage(conn, $"[Behavior] Invalid mode '{newMode}'. Use: guard | wander | dungeon_specific");
                return true;
            }

            try
            {
                using (var db = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(db,
                        "INSERT OR REPLACE INTO zone_behaviors (zone_name, behavior_mode, enabled) VALUES (@z, @m, 1)",
                        ("@z", zoneName), ("@m", newMode));
                }
                sendMessage(conn, $"[Behavior] {zoneName} -> {newMode}");
                Debug.LogError($"[BEHAVIOR] {conn.LoginName} set {zoneName} to {newMode}");

                int cleared = CombatManager.Instance.ClearZoneMobs(zoneName);
                ZoneSpawnManager.Instance.ResetZoneAndInstances(zoneName);
                sendMessage(conn, $"[Behavior] Cleared {cleared} cached mobs. Re-enter zone to see changes.");
            }
            catch (Exception ex)
            {
                sendMessage(conn, $"[Behavior] Error: {ex.Message}");
            }
            return true;
        }

        // ═══════════════════════════════════════════════════════════════════
        // POISON MODIFIER
        // ═══════════════════════════════════════════════════════════════════
        private void ApplyDamageModifier(RRConnection conn, Action<RRConnection, string> sendMessage)
        {
            if (conn.ModifiersId == 0)
            {
                sendMessage(conn, "[Error] ModifiersId not set - respawn required");
                return;
            }

            string connId = conn.ConnId.ToString();
            PlayerState playerState = _server.GetPlayerState(connId);

            Debug.LogError($"[DAMAGE] ModifiersId: 0x{conn.ModifiersId:X4}");

            string modifierGcClass = "skills.creature.mods.Modifiers.Bravery";

            var writer = new LEWriter();
            writer.WriteByte(0x35);
            writer.WriteUInt16(conn.ModifiersId);
            writer.WriteByte(0x00);

            writer.WriteByte(0xFF);
            writer.WriteCString(modifierGcClass);

            writer.WriteByte(0x00);
            writer.WriteByte(0xFF);
            writer.WriteCString(modifierGcClass);
            writer.WriteUInt32(0x00000000);
            writer.WriteByte(0x00);

            _server.WritePlayerEntitySynch(conn, writer);

            writer.WriteByte(0x06);

            byte[] packet = writer.ToArray();
            Debug.LogError($"[DAMAGE] Packet ({packet.Length} bytes): {BitConverter.ToString(packet)}");

            _server.SendToClient(conn, packet);

            // Track for zone persistence
            _server.TrackModifierSent(conn.LoginName, modifierGcClass, 0x00000000);

            sendMessage(conn, "[Damage] Applied Bravery modifier (test)!");
        }

        private static uint GetClientThresholdFixed88(int targetLevel)
        {
            int[][] entries = new int[][] {
                new int[] { 2 << 8, 10 << 8 },
                new int[] { 3 << 8, 25 << 8 },
                new int[] { 4 << 8, 45 << 8 },
                new int[] { 5 << 8, 65 << 8 },
                new int[] { 100 << 8, 5000 << 8 }
            };
            int q = targetLevel << 8;
            int curveF88 = entries[entries.Length - 1][1];
            for (int i = 0; i < entries.Length; i++)
            {
                if (q <= entries[i][0])
                {
                    if (i == 0) { curveF88 = entries[0][1]; }
                    else
                    {
                        int lv0 = entries[i - 1][0], v0 = entries[i - 1][1];
                        int lv1 = entries[i][0], v1 = entries[i][1];
                        curveF88 = v0 + ((q - lv0) * (v1 - v0)) / (lv1 - lv0);
                    }
                    break;
                }
            }
            return (uint)((curveF88 >> 8) * 100);
        }
    }
}
