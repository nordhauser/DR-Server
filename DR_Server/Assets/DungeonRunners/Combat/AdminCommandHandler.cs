using System;
using System.Text;
using System.Collections.Generic;
using UnityEngine;
using DungeonRunners.Networking;
using DungeonRunners.Utilities;
using DungeonRunners.Data;
using DungeonRunners.Database;
using System.Linq;

namespace DungeonRunners.Combat
{
    public class AdminCommandHandler
    {
        // Binary-proven: admin.Admin_Invincible_Mod sets MAX_HIT_POINTS = 5000
        private const uint INVINCIBLE_HP_WIRE = 5000 * 256;

        // All single-word commands the client sends on channel 9
        private static readonly HashSet<string> KnownAdminCommands = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase)
        {
            "Invulnerable", "NotInvulnerable",
            "Invincible", "NotInvincible",
            "Invisible", "NotInvisible",
            "SuperSpeed", "NoSuperSpeed",
            "LevelSelf",
            "GiveGoldSelf",
            "Location",
            "RemoveAllGold"
        };

        // TransferSelf commands contain spaces - matched by prefix
        // Binary strings: "TransferSelf thehub start", "TransferSelf town start", "TransferSelf pvp_town start"
        private static readonly string[] TransferCommands = new[]
        {
            "TransferSelf thehub start",
            "TransferSelf town start",
            "TransferSelf pvp_town start"
        };

        private static readonly Dictionary<string, string> TransferZoneMap = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            { "TransferSelf thehub start", "thehub" },
            { "TransferSelf town start", "town" },
            { "TransferSelf pvp_town start", "pvp_start" }
        };

        // Track active modifiers per connection
        private readonly Dictionary<int, HashSet<string>> _activeModsByConn
             = new Dictionary<int, HashSet<string>>();

        private readonly Dictionary<int, bool> _regenInProgress = new Dictionary<int, bool>();
        private readonly Dictionary<int, DateTime> _lastLevelUpTime = new Dictionary<int, DateTime>();
        private const double LEVEL_COOLDOWN_MS = 2000;  // 2 sec — client needs time to process OnLevelUp

        private uint _nextModInstanceId = 90000;

        // Callbacks set by server for zone changes and character access
        private Action<RRConnection, string> _onZoneChange;
        private Func<RRConnection, SavedCharacter> _getSavedCharacter;
        // Level-up callback: (conn, playerState, oldLevel, newLevel) — server sends XP packets + HP sync
        private Action<RRConnection, PlayerState, int, int> _onLevelUp;
        // Modifier tracking callbacks — server tracks for zone persistence
        private Action<string, string, uint> _onModifierAdded;   // (loginName, gcType, instanceId)
        private Action<string, uint> _onModifierRemoved;          // (loginName, instanceId)
        // Admin "CompleteQuest <instanceId>" callback (binary: 0x46bbb0 sends "CompleteQuest %u" on channel 9)
        private Action<RRConnection, uint> _onCompleteQuest;
        private Action<RRConnection, LEWriter> _writePlayerSynch;

        public void SetServerCallbacks(
            Action<RRConnection, string> onZoneChange,
            Func<RRConnection, SavedCharacter> getSavedCharacter,
            Action<RRConnection, PlayerState, int, int> onLevelUp = null,
            Action<string, string, uint> onModifierAdded = null,
            Action<string, uint> onModifierRemoved = null,
            Action<RRConnection, uint> onCompleteQuest = null,
            Action<RRConnection, LEWriter> writePlayerSynch = null)
        {
            _onZoneChange = onZoneChange;
            _getSavedCharacter = getSavedCharacter;
            _onLevelUp = onLevelUp;
            _onModifierAdded = onModifierAdded;
            _onModifierRemoved = onModifierRemoved;
            _onCompleteQuest = onCompleteQuest;
            _writePlayerSynch = writePlayerSynch;
        }

        public bool TryHandleAdminCommand(
            RRConnection conn,
            byte messageType,
            byte[] data,
            PlayerState playerState,
            Action<RRConnection, string> onStatusMessage,
            Action<RRConnection, byte[]> sendPacket)
        {
            if (data == null || data.Length == 0) return false;

            // Intercept "CompleteQuest <instanceId>" admin cheat (binary: 0x46bbb0 sends "CompleteQuest %u" on channel 9).
            // The instanceId is the decimal-formatted u32 quest instance id from quest+0x68.
            string raw = Encoding.ASCII.GetString(data).TrimEnd('\0');
            int cqIdx = raw.IndexOf("CompleteQuest ", StringComparison.OrdinalIgnoreCase);
            if (cqIdx >= 0)
            {
                string tail = raw.Substring(cqIdx + "CompleteQuest ".Length).TrimEnd('\0', ' ');
                // Stop at first non-digit (the binary uses %u so format is pure decimal digits)
                int digitEnd = 0;
                while (digitEnd < tail.Length && char.IsDigit(tail[digitEnd])) digitEnd++;
                if (digitEnd > 0 && uint.TryParse(tail.Substring(0, digitEnd), out uint instanceId))
                {
                    Debug.LogError($"[ADMIN] CompleteQuest instanceId={instanceId} from conn={conn.ConnId}");
                    if (_onCompleteQuest != null)
                    {
                        _onCompleteQuest(conn, instanceId);
                        onStatusMessage?.Invoke(conn, $"Admin: Completing quest {instanceId}");
                    }
                    else
                    {
                        Debug.LogError("[ADMIN] CompleteQuest callback not wired up");
                        onStatusMessage?.Invoke(conn, "Admin: CompleteQuest not wired");
                    }
                    return true;
                }
                Debug.LogError($"[ADMIN] CompleteQuest could not parse instanceId from tail: \"{tail}\"");
            }

            string command = TryExtractCommandString(data);
            if (string.IsNullOrEmpty(command)) return false;

            Debug.LogError($"[ADMIN] Received: \"{command}\" from conn={conn.ConnId}");
            ProcessAdminCommand(conn, command, playerState, onStatusMessage, sendPacket);
            return true;
        }

        private string TryExtractCommandString(byte[] data)
        {
            // Try exact match at various offsets
            string full = ExtractString(data, 0);
            if (!string.IsNullOrEmpty(full) && KnownAdminCommands.Contains(full))
                return full;

            if (data.Length > 1)
            {
                string s = ExtractString(data, 1);
                if (!string.IsNullOrEmpty(s) && KnownAdminCommands.Contains(s))
                    return s;
            }

            if (data.Length > 2)
            {
                string s = ExtractString(data, 2);
                if (!string.IsNullOrEmpty(s) && KnownAdminCommands.Contains(s))
                    return s;
            }

            // Check for TransferSelf commands (contain spaces, need special handling)
            string entire = Encoding.ASCII.GetString(data).TrimEnd('\0');
            foreach (var transfer in TransferCommands)
            {
                if (entire.IndexOf(transfer, StringComparison.OrdinalIgnoreCase) >= 0)
                    return transfer;
            }

            // NOTE: previously there was a substring-scan fallback here that
            // matched any packet containing an admin command name (e.g. "LevelSelf")
            // anywhere in the bytes. That caused normal chat/zone packets to spuriously
            // trigger admin commands when an admin player was connected, producing the
            // "zoned and suddenly gained 20 levels" bug. The 3 strict ExtractString checks
            // at offsets 0/1/2 above are the legitimate dispatch path. If a future admin
            // command needs a looser match, add it as its own explicit check above, like
            // CompleteQuest does with raw.IndexOf.

            Debug.LogError($"[ADMIN] Channel 9 not admin command: {BitConverter.ToString(data)}");
            return null;
        }

        private string ExtractString(byte[] data, int offset)
        {
            if (offset >= data.Length) return null;
            int end = offset;
            while (end < data.Length && data[end] != 0) end++;
            if (end == offset) return null;
            return Encoding.ASCII.GetString(data, offset, end - offset);
        }

        private void ProcessAdminCommand(
            RRConnection conn,
            string command,
            PlayerState playerState,
            Action<RRConnection, string> onStatusMessage,
            Action<RRConnection, byte[]> sendPacket)
        {
            if (!_activeModsByConn.ContainsKey(conn.ConnId))
                _activeModsByConn[conn.ConnId] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Handle TransferSelf commands
            if (TransferZoneMap.TryGetValue(command, out string zoneName))
            {
                if (_onZoneChange != null)
                {
                    onStatusMessage?.Invoke(conn, $"Admin: Transferring to {zoneName}");
                    _onZoneChange(conn, zoneName);
                }
                else
                {
                    onStatusMessage?.Invoke(conn, "Admin: Zone transfer not available");
                }
                return;
            }

            switch (command)
            {
                // ═══════════ INVULNERABLE ═══════════
                case "Invulnerable":
                    playerState.IsDamageImmune = true;
                    SendAddModifier(conn, "Invulnerable", playerState, sendPacket);
                    onStatusMessage?.Invoke(conn, "Admin: Invulnerable ON");
                    break;
                case "NotInvulnerable":
                    SendRemoveModifier(conn, "Invulnerable", playerState, sendPacket);
                    if (!HasActiveMod(conn.ConnId, "Invincible") && !HasActiveMod(conn.ConnId, "Invisible"))
                        playerState.IsDamageImmune = false;
                    onStatusMessage?.Invoke(conn, "Admin: Invulnerable OFF");
                    break;

                // ═══════════ INVINCIBLE ═══════════
                case "Invincible":
                    playerState.IsDamageImmune = true;
                    playerState.ApplyMaxHPModifier(INVINCIBLE_HP_WIRE);
                    _regenInProgress[conn.ConnId] = true;
                    SendAddModifier(conn, "Invincible", playerState, sendPacket);
                    onStatusMessage?.Invoke(conn, "Admin: Invincible ON");
                    break;
                case "NotInvincible":
                    if (_regenInProgress.ContainsKey(conn.ConnId) && _regenInProgress[conn.ConnId])
                    {
                        onStatusMessage?.Invoke(conn, "Admin: Wait for HP sync to complete");
                        break;
                    }
                    SendRemoveModifier(conn, "Invincible", playerState, sendPacket);
                    playerState.RemoveMaxHPModifier(INVINCIBLE_HP_WIRE);
                    if (!HasActiveMod(conn.ConnId, "Invulnerable") && !HasActiveMod(conn.ConnId, "Invisible"))
                        playerState.IsDamageImmune = false;
                    onStatusMessage?.Invoke(conn, "Admin: Invincible OFF");
                    break;

                // ═══════════ INVISIBLE ═══════════
                case "Invisible":
                    playerState.IsDamageImmune = true;
                    playerState.IsInvisible = true;
                    SendAddModifier(conn, "Invisible", playerState, sendPacket);
                    onStatusMessage?.Invoke(conn, "Admin: Invisible ON");
                    break;
                case "NotInvisible":
                    SendRemoveModifier(conn, "Invisible", playerState, sendPacket);
                    if (!HasActiveMod(conn.ConnId, "Invulnerable") && !HasActiveMod(conn.ConnId, "Invincible"))
                        playerState.IsDamageImmune = false;
                    playerState.IsInvisible = false;
                    onStatusMessage?.Invoke(conn, "Admin: Invisible OFF");
                    break;

                // ═══════════ SUPERSPEED ═══════════
                case "SuperSpeed":
                    SendAddModifier(conn, "SuperSpeed", playerState, sendPacket);
                    onStatusMessage?.Invoke(conn, "Admin: SuperSpeed ON");
                    break;
                case "NoSuperSpeed":
                    SendRemoveModifier(conn, "SuperSpeed", playerState, sendPacket);
                    onStatusMessage?.Invoke(conn, "Admin: SuperSpeed OFF");
                    break;

                // ═══════════ LEVELSELF ═══════════
                case "LevelSelf":
                    {
                        // Cooldown — client needs time to process OnLevelUp before next level
                        if (_lastLevelUpTime.TryGetValue(conn.ConnId, out DateTime last)
                            && (DateTime.UtcNow - last).TotalMilliseconds < LEVEL_COOLDOWN_MS)
                        {
                            onStatusMessage?.Invoke(conn, "Admin: Wait a moment between levels");
                            break;
                        }

                        var savedChar = _getSavedCharacter?.Invoke(conn);
                        if (savedChar != null && savedChar.level < 100)
                        {
                            _lastLevelUpTime[conn.ConnId] = DateTime.UtcNow;
                            int oldLevel = savedChar.level;
                            savedChar.level++;
                            CharacterRepository.SaveCharacter(savedChar);
                            // Update runtime PlayerState so auto-save doesn't overwrite
                            playerState.InitializeStats(savedChar.className ?? "Fighter", savedChar.level);
                            playerState.Experience = savedChar.experience;
                            onStatusMessage?.Invoke(conn, $"Level: {savedChar.level}");
                            Debug.LogError($"[ADMIN] LevelSelf: {savedChar.name} -> level {savedChar.level}");

                            // Send XP packet + HP sync to client so it plays level-up effects
                            _onLevelUp?.Invoke(conn, playerState, oldLevel, savedChar.level);
                        }
                        else
                        {
                            onStatusMessage?.Invoke(conn, "Admin: Already max level");
                        }
                        break;
                    }

                // ═══════════ GIVEGOLDSELF ═══════════
                case "GiveGoldSelf":
                    {
                        var savedChar = _getSavedCharacter?.Invoke(conn);
                        if (savedChar != null)
                        {
                            uint addAmount = 1000000;
                            savedChar.gold += addAmount;
                            CharacterRepository.SaveCharacter(savedChar);
                            onStatusMessage?.Invoke(conn, $"Admin: +1,000,000 gold (total: {savedChar.gold})");
                            Debug.LogError($"[ADMIN] GiveGoldSelf: {savedChar.name} gold={savedChar.gold}");

                            // Send gold update to client's UnitContainer (AddCurrency 0x20)
                            if (conn.UnitContainerId != 0)
                            {
                                var goldWriter = new LEWriter();
                                goldWriter.WriteByte(0x07);  // BeginStream
                                goldWriter.WriteByte(0x35);  // ComponentUpdate
                                goldWriter.WriteUInt16(conn.UnitContainerId);
                                goldWriter.WriteByte(0x20);  // AddCurrency
                                goldWriter.WriteUInt32(addAmount);    // amount
                                goldWriter.WriteByte(0x00);           // CurrencySource
                                goldWriter.WriteUInt32(0x00000000);   // entityHandle
                                goldWriter.WriteByte(0x01);           // notifyFlag
                                _writePlayerSynch(conn, goldWriter);
                                goldWriter.WriteByte(0x06);  // EndStream
                                sendPacket?.Invoke(conn, goldWriter.ToArray());
                                Debug.LogError($"[ADMIN] Sent gold update +{addAmount} to UC 0x{conn.UnitContainerId:X4}");
                            }
                        }
                        break;
                    }

                // ═══════════ REMOVEALLGOLD ═══════════
                case "RemoveAllGold":
                    {
                        var savedChar = _getSavedCharacter?.Invoke(conn);
                        if (savedChar != null)
                        {
                            uint oldGold = savedChar.gold;
                            savedChar.gold = 0;
                            CharacterRepository.SaveCharacter(savedChar);
                            onStatusMessage?.Invoke(conn, "Admin: All gold removed");
                            Debug.LogError($"[ADMIN] RemoveAllGold: {savedChar.name} gold=0");

                            // Send gold removal to client's UnitContainer (RemoveCurrency 0x21)
                            if (conn.UnitContainerId != 0 && oldGold > 0)
                            {
                                var goldWriter = new LEWriter();
                                goldWriter.WriteByte(0x07);  // BeginStream
                                goldWriter.WriteByte(0x35);  // ComponentUpdate
                                goldWriter.WriteUInt16(conn.UnitContainerId);
                                goldWriter.WriteByte(0x21);  // RemoveCurrency
                                goldWriter.WriteUInt32(oldGold);      // amount to remove
                                goldWriter.WriteByte(0x00);           // CurrencySource
                                _writePlayerSynch(conn, goldWriter);
                                goldWriter.WriteByte(0x06);  // EndStream
                                sendPacket?.Invoke(conn, goldWriter.ToArray());
                                Debug.LogError($"[ADMIN] Sent gold removal -{oldGold} to UC 0x{conn.UnitContainerId:X4}");
                            }
                        }
                        break;
                    }

                // ═══════════ LOCATION ═══════════
                case "Location":
                    {
                        string locMsg = $"Pos=({conn.PlayerPosX:F1}, {conn.PlayerPosY:F1}, {conn.PlayerPosZ:F1}) heading={conn.PlayerHeading:F0} zone={conn.CurrentZoneName ?? "?"}";
                        onStatusMessage?.Invoke(conn, "Admin: " + locMsg);
                        Debug.LogError($"[LOC] {conn.LoginName ?? conn.ConnId.ToString()} {locMsg}");
                    }
                    break;

                default:
                    Debug.LogError($"[ADMIN] Unhandled: \"{command}\"");
                    break;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // MODIFIER PACKET BUILDERS
        // ═══════════════════════════════════════════════════════════════

        private readonly Dictionary<string, uint> _modInstanceIds = new Dictionary<string, uint>();

        private void SendAddModifier(RRConnection conn, string modName, PlayerState playerState, Action<RRConnection, byte[]> sendPacket)
        {
            var mods = _activeModsByConn[conn.ConnId];
            if (mods.Contains(modName)) return;
            mods.Add(modName);
            string gcType = $"admin.Admin_{modName}_Mod";
            uint instanceId = _nextModInstanceId++;
            string key = $"{conn.ConnId}_{modName}";
            _modInstanceIds[key] = instanceId;
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(conn.ModifiersId);
            writer.WriteByte(0x00);
            writer.WriteByte(0xFF);
            writer.WriteCString(gcType);
            writer.WriteUInt32(instanceId);
            writer.WriteByte(0);
            writer.WriteUInt32(0);
            writer.WriteUInt32(0);
            writer.WriteByte(0);
            _writePlayerSynch(conn, writer);
            writer.WriteByte(0x06);
            byte[] packet = writer.ToArray();
            Debug.LogError($"[ADMIN] ADD '{gcType}' instId={instanceId} pkt={BitConverter.ToString(packet)}");
            sendPacket(conn, packet);
            _onModifierAdded?.Invoke(conn.LoginName, gcType, instanceId);
        }

        private void SendRemoveModifier(RRConnection conn, string modName, PlayerState playerState, Action<RRConnection, byte[]> sendPacket)
        {
            var mods = _activeModsByConn[conn.ConnId];
            if (!mods.Contains(modName)) return;
            mods.Remove(modName);
            string key = $"{conn.ConnId}_{modName}";
            uint instanceId = _modInstanceIds.ContainsKey(key) ? _modInstanceIds[key] : 0;
            _modInstanceIds.Remove(key);
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x35);
            writer.WriteUInt16(conn.ModifiersId);
            writer.WriteByte(0x01);
            writer.WriteUInt32(instanceId);
            _writePlayerSynch(conn, writer);
            writer.WriteByte(0x06);
            byte[] packet = writer.ToArray();
            Debug.LogError($"[ADMIN] REMOVE '{modName}' instId={instanceId} pkt={BitConverter.ToString(packet)}");
            sendPacket(conn, packet);
            _onModifierRemoved?.Invoke(conn.LoginName, instanceId);
        }

        public void ClearRegenFlag(int connId)
        {
            _regenInProgress[connId] = false;
        }
        public bool IsRegenActive(int connId)
        {
            return _regenInProgress.ContainsKey(connId) && _regenInProgress[connId];
        }
        public void OnPlayerDisconnected(int connId)
        {
            _activeModsByConn.Remove(connId);
            _regenInProgress.Remove(connId);
            _lastLevelUpTime.Remove(connId);
        }

        public bool HasActiveMod(int connId, string modName)
        {
            return _activeModsByConn.TryGetValue(connId, out var mods) && mods.Contains(modName);
        }

        /// <summary>
        /// Re-send all active admin modifiers after zone transition.
        /// The entity is new, so we need fresh processAddModifier packets.
        /// </summary>
        public void ResendActiveModifiers(RRConnection conn, PlayerState playerState, Action<RRConnection, byte[]> sendPacket)
        {
            if (!_activeModsByConn.TryGetValue(conn.ConnId, out var mods) || mods.Count == 0)
                return;

            Debug.LogError($"[ADMIN] Re-sending {mods.Count} active modifiers for {conn.ConnId} after zone transition");
            foreach (var modName in mods.ToList())
            {
                string gcType = $"admin.Admin_{modName}_Mod";
                uint instanceId = _nextModInstanceId++;
                string key = $"{conn.ConnId}_{modName}";
                _modInstanceIds[key] = instanceId;

                var writer = new LEWriter();
                writer.WriteByte(0x07);
                writer.WriteByte(0x35);
                writer.WriteUInt16(conn.ModifiersId);
                writer.WriteByte(0x00);
                writer.WriteByte(0xFF);
                writer.WriteCString(gcType);
                writer.WriteUInt32(instanceId);
                writer.WriteByte(0);
                writer.WriteUInt32(0);
                writer.WriteUInt32(0);
                writer.WriteByte(0);
                _writePlayerSynch(conn, writer);
                writer.WriteByte(0x06);

                byte[] packet = writer.ToArray();
                sendPacket(conn, packet);
                Debug.LogError($"[ADMIN] Re-sent '{gcType}' instId={instanceId}");
            }
        }
    }
}
