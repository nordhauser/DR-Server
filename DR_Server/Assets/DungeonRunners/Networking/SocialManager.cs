// ═══════════════════════════════════════════════════════════════════════════════
// SocialManager.cs — Channel 0x0C (UserManagerClient) server implementation
// ═══════════════════════════════════════════════════════════════════════════════
// Binary RE verified against DungeonRunners.exe + PDB:
//
// SERVER → CLIENT (type byte in data[1]):
//   0x00  processConnectedMessage    @ VA 0x602850  — [string name\0]
//   0x01  processRostersMessage      @ VA 0x6028F0  — [byte pub1][byte pub2][uint32 friendCount][...names][uint32 detailedCount=0]
//   0x02  processAddContactMessage   @ VA 0x602EA0  — [string name\0][uint32 status]
//   0x03  processRemoveContactMessage@ VA 0x6030B0  — [string name\0][uint32 status]
//   0x04  processAddIgnoreMessage    @ VA 0x603200  — [string name\0][uint32 status]
//   0x05  processRemoveIgnoreMessage @ VA 0x603320  — [string name\0][uint32 status]
//   0x06  processRosterNotifyMessage @ VA 0x603460  — [string name\0][uint32 reason] (3=online, 4=offline)
//   0x07  processRosterPropertyChanged@VA 0x6037D0  — property bag (zone/level changes)
//   0x08  processFriendsPublicityMsg @ VA 0x603BA0  — [byte flag]
//   0x09  processUsersMessage (Who)  @ VA 0x603C40  — per entry: [uint32 avatarEntityId][uint32 charSqlId→obj+0x14][string name\0][string zone\0][uint16 level][uint32 groupId][byte isOnline] sentinel: [uint32 0]
//   0x0A  processUserListEventMessage@ VA 0x604230  — group change events
//   0x0B  processSetBusy             @ VA 0x6048D0  — [byte busy]
//
// CLIENT → SERVER (type byte = messageType from HandleChannelMessage):
//   0x00  connect          — (empty)                    → triggers server 0x00 response
//   0x01  requestRosters   — (empty)                    → triggers server 0x01 response
//   0x03  addContact       — [string name\0]            → server 0x02 confirmation
//   0x04  removeContact    — [string name\0]            → server 0x03 confirmation
//   0x05  addIgnore        — [string name\0]            → server 0x04 confirmation
//   0x06  removeIgnore     — [string name\0]            → server 0x05 confirmation
//   0x07  requestUserList  — (empty)                    → server 0x09 who list
//   0x09  setBusy          — [byte busy]                → server 0x0B confirmation
//   0x0A  requestJoinGroup — [uint32 groupId]           → (handled by GroupManager)
//   0x0B  setFriendsPublicity — [byte flag]             → server 0x08 confirmation
//
// PDB symbols:
//   sendRosterMessage           @ VA 0x604970  — writes [byte type][string name\0]
//   onAddFriend                 @ VA 0x470710  — calls sendRosterMessage(3, name)
//   onRemoveFriend              @ VA 0x470740  — calls sendRosterMessage(4, name)
//   onIgnore                    @ VA 0x470770  — calls sendRosterMessage(5, name)
//   onStopIgnoring              @ VA 0x4707A0  — calls sendRosterMessage(6, name)
//   requestRosters              @ VA 0x601EC0  — auto-called by processConnectedMessage
//   requestUserList             @ VA 0x6025B0  — called when Who tab opened
//   setBusy                     @ VA 0x602200  — type 0x09 + [byte flag]
//   requestSetFriendsPublicity  @ VA 0x602480  — type 0x0B + [byte flag]
//
// readString @ VA 0x62BCE0: reads bytes until 0x00 null terminator (= WriteCString format)
// writeString @ VA 0x62C260: writes [data bytes][0x00] (= same null-terminated format)
// Who list sentinel: BSS @ 0x932EE4 = 0x00000000 (charSqlId == 0 terminates loop)
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using DungeonRunners.Data;
using DungeonRunners.Database;
using DungeonRunners.Managers;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    public class SocialManager
    {
        private static SocialManager _instance;
        public static SocialManager Instance => _instance ??= new SocialManager();

        // Per-CHARACTER social data (keyed by character name, NOT login name)
        // Original game stored per-account but that causes stale friend entries
        // when switching characters on the same account.
        private Dictionary<string, List<string>> _friends = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<string>> _ignores = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, bool> _busyFlags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, bool> _publicityFlags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // Reverse lookup: character name → login name (for friend notifications)
        private Dictionary<string, string> _charNameToLogin = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Active connections (set by server on login/logout)
        private Dictionary<string, RRConnection> _onlineUsers = new Dictionary<string, RRConnection>(StringComparer.OrdinalIgnoreCase);

        public SocialManager()
        {
            EnsureSocialTables();
            LoadSocialData();
        }

        // ═══════════════════════════════════════════════════════════════
        // PUBLIC API — called by UnityGameServer
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Register a player as online. Call during character selection / login.
        /// </summary>
        public void PlayerOnline(string loginName, string characterName, RRConnection conn,
            Action<RRConnection, byte, byte, byte[]> sendCompressed = null)
        {
            _onlineUsers[loginName] = conn;
            _charNameToLogin[characterName] = loginName;

            if (!_friends.ContainsKey(characterName))
                _friends[characterName] = new List<string>();
            if (!_ignores.ContainsKey(characterName))
                _ignores[characterName] = new List<string>();

            DRLog.Social($"Player online: {characterName} ({loginName}) total={_onlineUsers.Count}");

            // Push updated who list to all OTHER online players so they see the new player
            // Also notify friends this player came online
            // Binary-verified: reason=3 SETS bit 0 of [entry+0x14] = ONLINE display
            if (sendCompressed != null)
            {
                NotifyFriendsOfStatus(loginName, characterName, 3, sendCompressed);  // 3 = ONLINE

                // Push updated who list to all other players
                foreach (var kvp in _onlineUsers)
                {
                    if (kvp.Value != null && kvp.Value.IsConnected && kvp.Value.ConnId != conn.ConnId)
                    {
                        DRLog.Social($"  Auto-pushing who list to {kvp.Key} (new player joined)");
                        SendWhoList(kvp.Value, sendCompressed);
                    }
                }
            }
        }

        /// <summary>
        /// Unregister a player. Call on disconnect.
        /// </summary>
        public void PlayerOffline(string loginName, string characterName, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            DRLog.Social($"PlayerOffline called: login='{loginName}' char='{characterName}' onlineUsers.Count={_onlineUsers.Count} containsKey={_onlineUsers.ContainsKey(loginName)}");

            // Notify friends this player went offline BEFORE removing from online list
            // Binary-verified: reason=4 CLEARS bit 0, sets bit 2 = OFFLINE display
            NotifyFriendsOfStatus(loginName, characterName, 4, sendCompressed);  // 4 = OFFLINE

            bool removed = _onlineUsers.Remove(loginName);
            bool charRemoved = _charNameToLogin.Remove(characterName);

            DRLog.Social($"Player offline: {characterName} ({loginName}) removed={removed} charRemoved={charRemoved} remaining={_onlineUsers.Count}");

            // Log remaining online users for debug
            foreach (var kvp in _onlineUsers)
                DRLog.Social($"  Still online: '{kvp.Key}' connected={kvp.Value?.IsConnected}");

            // Push updated who list to all remaining players so they see the change immediately
            foreach (var kvp in _onlineUsers)
            {
                if (kvp.Value != null && kvp.Value.IsConnected)
                {
                    DRLog.Social($"  Auto-pushing who list to {kvp.Key}");
                    SendWhoList(kvp.Value, sendCompressed);
                }
            }
        }

        /// <summary>
        /// Fallback cleanup when character name isn't available at disconnect time.
        /// Removes from _onlineUsers and attempts to clean _charNameToLogin.
        /// </summary>
        public void ForceRemoveOnline(string loginName)
        {
            DRLog.Social($"ForceRemoveOnline: login='{loginName}' containsKey={_onlineUsers.ContainsKey(loginName)}");

            _onlineUsers.Remove(loginName);

            // Clean charNameToLogin by finding any entry that maps to this login
            string charToRemove = null;
            foreach (var kvp in _charNameToLogin)
            {
                if (kvp.Value.Equals(loginName, StringComparison.OrdinalIgnoreCase))
                {
                    charToRemove = kvp.Key;
                    break;
                }
            }
            if (charToRemove != null)
                _charNameToLogin.Remove(charToRemove);

            DRLog.Social($"Force removed online user: {loginName} remaining={_onlineUsers.Count}");
        }

        /// <summary>
        /// Handle incoming channel 0x0C message from client.
        /// </summary>
        public void HandleMessage(RRConnection conn, byte messageType, byte[] data,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            string hex = data != null ? BitConverter.ToString(data, 0, Math.Min(data.Length, 40)) : "null";
            DRLog.Social($"══ INCOMING ch=0x0C type=0x{messageType:X2} len={data?.Length ?? 0} from={conn.LoginName} hex={hex}");

            switch (messageType)
            {
                case 0x00: // connect
                    DRLog.Social("→ Client sent CONNECT (0x00)");
                    HandleConnect(conn, sendCompressed);
                    break;
                case 0x01: // requestRosters
                    DRLog.Social("→ Client sent REQUEST_ROSTERS (0x01)");
                    HandleRequestRosters(conn, sendCompressed);
                    break;
                case 0x03: // addContact (add friend)
                    DRLog.Social($"→ Client sent ADD_CONTACT (0x03)");
                    HandleAddContact(conn, data, sendCompressed);
                    break;
                case 0x04: // removeContact (remove friend)
                    DRLog.Social($"→ Client sent REMOVE_CONTACT (0x04)");
                    HandleRemoveContact(conn, data, sendCompressed);
                    break;
                case 0x05: // addIgnore
                    DRLog.Social($"→ Client sent ADD_IGNORE (0x05)");
                    HandleAddIgnore(conn, data, sendCompressed);
                    break;
                case 0x06: // removeIgnore
                    DRLog.Social($"→ Client sent REMOVE_IGNORE (0x06)");
                    HandleRemoveIgnore(conn, data, sendCompressed);
                    break;
                case 0x07: // requestUserList (Who)
                    DRLog.Social("→ Client sent REQUEST_WHO (0x07)");
                    HandleRequestUserList(conn, data, sendCompressed);
                    break;
                case 0x08: // cancelUserList (cancelUserListInternal @ 0x604A70)
                    DRLog.Social("→ Client sent CANCEL_USER_LIST (0x08) — acknowledged");
                    break;
                case 0x09: // setBusy
                    DRLog.Social("→ Client sent SET_BUSY (0x09)");
                    HandleSetBusy(conn, data, sendCompressed);
                    break;
                case 0x0A: // requestJoinGroup
                    DRLog.Social("→ Client sent JOIN_GROUP (0x0A)");
                    HandleRequestJoinGroup(conn, data, sendCompressed);
                    break;
                case 0x0B: // requestSetFriendsPublicity
                    DRLog.Social("→ Client sent SET_PUBLICITY (0x0B)");
                    HandleSetFriendsPublicity(conn, data, sendCompressed);
                    break;
                default:
                    DRLog.Social($"→ UNHANDLED type 0x{messageType:X2}");
                    break;
            }
        }

        /// <summary>
        /// Send Connected proactively during login.
        /// Binary: processConnectedMessage @ 0x602850 sets online flag,
        /// then auto-calls requestRosters @ 0x601EC0 which sends type 0x01
        /// on TChannelManager channel 3. Our case 3 handler routes it to
        /// HandleRequestRosters which responds with the Roster packet.
        /// DO NOT send Roster proactively — mixing channel 0x0C (Connected)
        /// and channel 0x03 (Roster) back-to-back corrupts the DFCMessage stream.
        /// </summary>
        public void SendLoginSocialInit(RRConnection conn, string characterName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            // Type 0x00: processConnectedMessage → sets bit 0 of [+0x9C] (online flag)
            SendConnectedMessage(conn, characterName, sendCompressed);

            // Type 0x01: processRostersMessage → sets bit 1 of [+0xa4] (roster loaded)
            // + bit 2 via pub1 byte (friends available). Enables friends tab + Add Friend.
            string login = conn.LoginName ?? "";
            var friends = GetFriends(characterName);
            var ignores = GetIgnores(characterName);
            bool publicity = _publicityFlags.ContainsKey(login) && _publicityFlags[login];
            SendRosterMessage(conn, friends, ignores, publicity, sendCompressed);

            DRLog.Social($"Sent connected + roster for {characterName} (friends={friends.Count})");
            // NOTE: Online notifications for existing friends are sent in HandleRequestRosters,
            // AFTER the client has processed the roster and has the friend list loaded.
        }

        // ═══════════════════════════════════════════════════════════════
        // MESSAGE HANDLERS
        // ═══════════════════════════════════════════════════════════════

        private void HandleConnect(RRConnection conn, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            // DO NOT respond here. SendLoginSocialInit (from HandleCharacterPlay) sends
            // Connected + Roster as the sole path. HandleConnect fires before PlayerOnline
            // so GetCharacterName returns wrong name. Responding here creates duplicate
            // Connected packets that collide with SendLoginSocialInit's Connected + Roster.
            DRLog.Social($"HandleConnect: suppressed (SendLoginSocialInit handles it)");
        }

        private void HandleRequestRosters(RRConnection conn, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            // Client requested roster data — respond with type 0x01
            string login = conn.LoginName ?? "";
            string charName = GetCharacterNameForLogin(login);
            var friends = GetFriends(charName);
            var ignores = GetIgnores(charName);
            bool publicity = _publicityFlags.ContainsKey(login) && _publicityFlags[login];

            SendRosterMessage(conn, friends, ignores, publicity, sendCompressed);
            DRLog.Social($"Sent roster: {friends.Count} friends, {ignores.Count} ignores");

            // Send RosterNotify for FRIENDS ONLY.
            foreach (var friendName in friends)
            {
                string friendLogin = null;
                bool isOnline = _charNameToLogin.TryGetValue(friendName, out friendLogin)
                    && _onlineUsers.ContainsKey(friendLogin);

                SendRosterNotify(conn, friendName, 3, sendCompressed);

                if (isOnline)
                {
                    RRConnection friendConn = _onlineUsers[friendLogin];
                    SendRosterPropertyChanged(conn, friendName,
                        friendConn.CurrentZoneName ?? "unknown", friendConn.PlayerLevel, sendCompressed);
                    DRLog.Social($"  → Friend '{friendName}' ONLINE, sent reason=3 + properties");
                }
                else
                {
                    SendRosterNotify(conn, friendName, 4, sendCompressed);
                    DRLog.Social($"  → Friend '{friendName}' OFFLINE, sent reason=3 then reason=4");
                }
            }
        }

        private void HandleAddContact(RRConnection conn, byte[] data, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            string friendName = ReadNameFromPayload(data);
            if (string.IsNullOrEmpty(friendName))
            {
                SendAddContactResponse(conn, friendName ?? "", 1, sendCompressed); // status 1 = error
                return;
            }

            string login = conn.LoginName ?? "";
            string ownCharName = GetCharacterNameForLogin(login);
            var friends = GetFriends(ownCharName);
            if (!string.IsNullOrEmpty(ownCharName) && ownCharName.Equals(friendName, StringComparison.OrdinalIgnoreCase))
            {
                SendAddContactResponse(conn, friendName, 1, sendCompressed); // error
                return;
            }

            // Check duplicate
            if (friends.Any(f => f.Equals(friendName, StringComparison.OrdinalIgnoreCase)))
            {
                SendAddContactResponse(conn, friendName, 2, sendCompressed); // already exists
                return;
            }

            friends.Add(friendName);
            SaveAddFriend(ownCharName, friendName);

            // Also remove from ignores — can't be both friend AND ignored
            var ignoreList = GetIgnores(ownCharName);
            if (ignoreList.RemoveAll(i => i.Equals(friendName, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                SaveRemoveIgnore(ownCharName, friendName);
                DRLog.Social($"  Also removed '{friendName}' from ignore list");
            }

            // Status 0 = success
            SendAddContactResponse(conn, friendName, 0, sendCompressed);
            DRLog.Social($"{conn.LoginName} added friend: {friendName}");

            // If the friend is currently online, send reason=3 (ONLINE) to the ADDER
            // Binary-verified: reason=3 creates entry in +0xB8 with bit 0 set = ONLINE
            string friendLogin = null;
            if (_charNameToLogin.TryGetValue(friendName, out friendLogin) && _onlineUsers.ContainsKey(friendLogin))
            {
                SendRosterNotify(conn, friendName, 3, sendCompressed);  // 3 = ONLINE
                RRConnection friendConn = _onlineUsers[friendLogin];
                SendRosterPropertyChanged(conn, friendName,
                    friendConn.CurrentZoneName ?? "unknown", friendConn.PlayerLevel, sendCompressed);
                DRLog.Social($"  → Friend {friendName} is online, sent reason=3 + properties");
                // TODO S64: Binary has pending friend request flow (status codes 0x2C-0x2E, 0x37)
                // Need to RE processAddContactMessage status dispatch to implement confirm UI
                // For now: one-directional add only
            }
        }

        private void HandleRemoveContact(RRConnection conn, byte[] data, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            string friendName = ReadNameFromPayload(data);
            if (string.IsNullOrEmpty(friendName))
            {
                SendRemoveContactResponse(conn, friendName ?? "", 1, sendCompressed);
                return;
            }

            string login = conn.LoginName ?? "";
            string charName = GetCharacterNameForLogin(login);
            var friends = GetFriends(charName);
            friends.RemoveAll(f => f.Equals(friendName, StringComparison.OrdinalIgnoreCase));
            SaveRemoveFriend(charName, friendName);

            SendRemoveContactResponse(conn, friendName, 0, sendCompressed);
            DRLog.Social($"{conn.LoginName} ({charName}) removed friend: {friendName}");
        }

        private void HandleAddIgnore(RRConnection conn, byte[] data, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            string ignoreName = ReadNameFromPayload(data);
            if (string.IsNullOrEmpty(ignoreName))
            {
                SendAddIgnoreResponse(conn, ignoreName ?? "", 1, sendCompressed);
                return;
            }

            string login = conn.LoginName ?? "";
            string charName = GetCharacterNameForLogin(login);
            var ignores = GetIgnores(charName);

            if (ignores.Any(i => i.Equals(ignoreName, StringComparison.OrdinalIgnoreCase)))
            {
                SendAddIgnoreResponse(conn, ignoreName, 2, sendCompressed);
                return;
            }

            ignores.Add(ignoreName);
            SaveAddIgnore(charName, ignoreName);

            // Also remove from friends — can't be both friend AND ignored
            var friends = GetFriends(charName);
            if (friends.RemoveAll(f => f.Equals(ignoreName, StringComparison.OrdinalIgnoreCase)) > 0)
            {
                SaveRemoveFriend(charName, ignoreName);
                DRLog.Social($"  Also removed '{ignoreName}' from friends list");
            }

            SendAddIgnoreResponse(conn, ignoreName, 0, sendCompressed);
            DRLog.Social($"{conn.LoginName} ignored: {ignoreName}");
        }

        private void HandleRemoveIgnore(RRConnection conn, byte[] data, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            string ignoreName = ReadNameFromPayload(data);
            if (string.IsNullOrEmpty(ignoreName))
            {
                SendRemoveIgnoreResponse(conn, ignoreName ?? "", 1, sendCompressed);
                return;
            }

            string login = conn.LoginName ?? "";
            string charName = GetCharacterNameForLogin(login);
            var ignores = GetIgnores(charName);
            ignores.RemoveAll(i => i.Equals(ignoreName, StringComparison.OrdinalIgnoreCase));
            SaveRemoveIgnore(charName, ignoreName);

            SendRemoveIgnoreResponse(conn, ignoreName, 0, sendCompressed);
            DRLog.Social($"{conn.LoginName} ({charName}) unignored: {ignoreName}");
        }

        private void HandleRequestUserList(RRConnection conn, byte[] data, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            // Build who list from all online players
            SendWhoList(conn, sendCompressed);
            DRLog.Social($"Sent who list to {conn.LoginName}");
        }

        private void HandleSetBusy(RRConnection conn, byte[] data, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            bool busy = data != null && data.Length >= 1 && data[0] != 0;
            string login = conn.LoginName ?? "";
            _busyFlags[login] = busy;

            // Respond with type 0x0B
            SendSetBusyResponse(conn, busy, sendCompressed);
            DRLog.Social($"{conn.LoginName} busy={busy}");
        }

        private void HandleRequestJoinGroup(RRConnection conn, byte[] data, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            // Parse groupId and forward to GroupManager
            if (data != null && data.Length >= 4)
            {
                var reader = new LEReader(data);
                uint groupId = reader.ReadUInt32();
                DRLog.Social($"{conn.LoginName} requested join group {groupId} — forwarding to GroupManager");
                // GroupManager integration handled externally
            }
        }

        private void HandleSetFriendsPublicity(RRConnection conn, byte[] data, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            bool pub = data != null && data.Length >= 1 && data[0] != 0;
            string login = conn.LoginName ?? "";
            _publicityFlags[login] = pub;

            // Respond with type 0x08
            SendFriendsPublicityResponse(conn, pub, sendCompressed);
            DRLog.Social($"{conn.LoginName} publicity={pub}");
        }

        // ═══════════════════════════════════════════════════════════════
        // PACKET BUILDERS — all formats verified against binary disasm
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Type 0x00: processConnectedMessage @ 0x602850
        /// Reads: readString(name) → sets [esi+0x9C] bit 0 (online flag)
        /// Then calls requestRosters() at 0x601EC0 automatically.
        /// </summary>
        private void SendConnectedMessage(RRConnection conn, string characterName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var w = new LEWriter();
            w.WriteByte(0x03);  // channel: TChannelManager slot 3 (UserManagerClient)
            w.WriteByte(0x00);  // type: processConnectedMessage
            w.WriteCString(characterName);  // readString reads until \0
            byte[] packet = w.ToArray();
            DRLog.Social($"◄◄ SENDING Connected (0x00) name='{characterName}' hex={BitConverter.ToString(packet)}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        /// <summary>
        /// Type 0x01: processRostersMessage @ 0x6028F0
        /// Reads: [byte pub1][byte pub2][uint32 friendCount][N × readString name]
        ///        [uint32 detailedEntryCount][N × RemoteUserSession with property bags]
        /// CRITICAL: The second uint32 is NOT ignore count — it's a count of detailed
        /// online-friend entries with full property bags (RemoteUserSession @ 0x602A72).
        /// Sending simple names here corrupts the DFCMessage stream and kills the channel.
        /// Ignores are handled individually via processAddIgnoreMessage (type 0x04).
        /// </summary>
        private void SendRosterMessage(RRConnection conn, List<string> friends, List<string> ignores,
            bool publicity, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var w = new LEWriter();
            w.WriteByte(0x03);  // channel
            w.WriteByte(0x01);  // type: processRostersMessage

            w.WriteByte(0x01);  // pub flag 1 — enables friends system
            w.WriteByte(0x01);  // pub flag 2 — enables Add As Friend context menu option

            // Binary-verified: processRostersMessage @ 0x6029B5 reads names into +0xA8.
            // +0xA8 = the IGNORE display list (confirmed by testing: names here show in Ignore tab).
            // +0xB8 = the FRIENDS display list (populated by RosterNotify reason=3/4).
            // So we send IGNORE names here, NOT friend names.
            w.WriteUInt32((uint)ignores.Count);
            foreach (var name in ignores)
                w.WriteCString(name);

            // Detailed entries — KEEP AT ZERO for safety.
            // +0xB8 entries are created by RosterNotify reason=3 instead.
            // This avoids any format risk with the complex RemoteUserSession structure.
            w.WriteUInt32(0);

            byte[] packet = w.ToArray();
            DRLog.Social($"◄◄ SENDING Roster (0x01) ignores={ignores.Count} hex={BitConverter.ToString(packet)}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        /// <summary>
        /// Type 0x02: processAddContactMessage @ 0x602EA0
        /// Reads: readString(name) → read uint32(status)
        /// Status 0 = success (creates CoreChatContact), nonzero = error (shows debug string)
        /// Debug: "Received Add Contact Result with status %s(%d)"
        /// </summary>
        private void SendAddContactResponse(RRConnection conn, string name, uint status,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var w = new LEWriter();
            w.WriteByte(0x03);
            w.WriteByte(0x02);  // type: processAddContactMessage
            w.WriteCString(name);
            w.WriteUInt32(status);  // 0 = success
            sendCompressed(conn, 0x01, 0x0F, w.ToArray());
        }

        /// <summary>
        /// Type 0x03: processRemoveContactMessage @ 0x6030B0
        /// Reads: readString(name) → read uint32(status)
        /// Status 0 = success (removes from roster, fires UI event at 0x59FECE)
        /// Debug: "Received Remove Contact Result with status %s(%d)"
        /// </summary>
        private void SendRemoveContactResponse(RRConnection conn, string name, uint status,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var w = new LEWriter();
            w.WriteByte(0x03);
            w.WriteByte(0x03);  // type: processRemoveContactMessage
            w.WriteCString(name);
            w.WriteUInt32(status);
            sendCompressed(conn, 0x01, 0x0F, w.ToArray());
        }

        /// <summary>
        /// Type 0x04: processAddIgnoreMessage @ 0x603200
        /// Reads: readString(name) → read uint32(status)
        /// Debug: "Recieved Add Ignore Result with status %s(%d)" (note: original typo)
        /// </summary>
        private void SendAddIgnoreResponse(RRConnection conn, string name, uint status,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var w = new LEWriter();
            w.WriteByte(0x03);
            w.WriteByte(0x04);  // type: processAddIgnoreMessage
            w.WriteCString(name);
            w.WriteUInt32(status);
            sendCompressed(conn, 0x01, 0x0F, w.ToArray());
        }

        /// <summary>
        /// Type 0x05: processRemoveIgnoreMessage @ 0x603320
        /// Reads: readString(name) → read uint32(status)
        /// Debug: "Received Remove Ignore Result with status %s(%d)"
        /// </summary>
        private void SendRemoveIgnoreResponse(RRConnection conn, string name, uint status,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var w = new LEWriter();
            w.WriteByte(0x03);
            w.WriteByte(0x05);  // type: processRemoveIgnoreMessage
            w.WriteCString(name);
            w.WriteUInt32(status);
            sendCompressed(conn, 0x01, 0x0F, w.ToArray());
        }

        /// <summary>
        /// Type 0x06: processRosterNotifyMessage @ 0x603460
        /// Reads: readString(name) → read uint32(reason)
        /// Binary-verified @ processRosterNotifyMessage 0x603460:
        /// Reason 3 @ 0x603727: or [+0x14], 3 → SETS bits 0+1 = ONLINE
        /// Reason 4 @ 0x603682: or 4, and 0xFE → CLEARS bit 0, SETS bit 2 = OFFLINE
        /// Sub-dispatch at 0x60365A: (reason - 3) → 0 = online, 1 = offline
        /// </summary>
        private void SendRosterNotify(RRConnection conn, string friendName, uint reason,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var w = new LEWriter();
            w.WriteByte(0x03);
            w.WriteByte(0x06);  // type: processRosterNotifyMessage
            w.WriteCString(friendName);
            w.WriteUInt32(reason);  // 3=online, 4=offline
            sendCompressed(conn, 0x01, 0x0F, w.ToArray());
        }

        /// <summary>
        /// Type 0x07: processRosterPropertyChanged @ 0x6037D0
        /// Binary-verified format: [name\0][uint32 propCount][N × (key\0 + value\0)]
        /// Updates entry's property map at +0x24, then calls 0x604F00 to refresh display.
        ///
        /// Function 0x604F00 reads these keys (VA-to-offset corrected for .rdata section):
        ///   Key "Level" (5 chars @ VA 0x84EF5C) → strtol → [entry+0x18] = character level
        ///   Key "World" (5 chars @ VA 0x8689E0) → string copy → [entry+0x1C]+[entry+0x20] = zone names
        ///   Key "Zone"  (4 chars @ VA 0x864478) → lookup via 0x600AC0 → zone display override
        ///
        /// NOTE: Original VA→offset formula (va - 0x401000 + 0x400) was WRONG for .rdata!
        /// .text delta=0x400C00, .rdata delta=0x401000 — 0x400 byte difference.
        /// "erLay" was actually "Level", garbage was actually "World", null was actually "Zone".
        /// </summary>
        private void SendRosterPropertyChanged(RRConnection conn, string friendName,
            string zoneName, int level,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var w = new LEWriter();
            w.WriteByte(0x03);
            w.WriteByte(0x07);  // type: processRosterPropertyChanged
            w.WriteCString(friendName);
            w.WriteUInt32(2);           // 2 properties
            w.WriteCString("Level");    // Key 1: binary @ VA 0x84EF5C (5 chars)
            w.WriteCString(level.ToString());
            w.WriteCString("World");    // Key 2: binary @ VA 0x8689E0 (5 chars)
            w.WriteCString(zoneName);
            byte[] packet = w.ToArray();
            DRLog.Social($"◄◄ SENDING PropertyChanged (0x07) for {friendName}: Level={level} World={zoneName}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        /// <summary>
        /// Type 0x08: processFriendsPublicityMessage @ 0x603BA0
        /// Reads: byte flag → sets bit 3 of [edi+0xA4]
        /// Fires UI event at 0x59FED2
        /// </summary>
        private void SendFriendsPublicityResponse(RRConnection conn, bool publicity,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var w = new LEWriter();
            w.WriteByte(0x03);
            w.WriteByte(0x08);  // type: processFriendsPublicityMessage
            w.WriteByte(publicity ? (byte)0x01 : (byte)0x00);
            sendCompressed(conn, 0x01, 0x0F, w.ToArray());
        }

        /// <summary>
        /// Type 0x09: processUsersMessage (Who list + Groups tab) @ 0x603C40
        /// Binary-verified: populates both Who tab and Groups tab via ch3.
        /// Entry loop sentinel = 0x00000000 (BSS @ 0x932EE4).
        /// Group listing loop follows entries — populates Groups tab.
        /// </summary>
        private void SendWhoList(RRConnection conn, Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var staleLogins = new List<string>();
            foreach (var kvp in _onlineUsers)
            {
                if (kvp.Value == null || !kvp.Value.IsConnected)
                    staleLogins.Add(kvp.Key);
            }
            foreach (var stale in staleLogins)
            {
                _onlineUsers.Remove(stale);
                string charToRemove = null;
                foreach (var kvp in _charNameToLogin)
                {
                    if (kvp.Value.Equals(stale, StringComparison.OrdinalIgnoreCase))
                    { charToRemove = kvp.Key; break; }
                }
                if (charToRemove != null)
                    _charNameToLogin.Remove(charToRemove);
            }

            var w = new LEWriter();
            w.WriteByte(0x03);
            w.WriteByte(0x09);

            int entryCount = 0;
            var openGroups = new List<Group>();

            // Binary @0x60403B: LAST online user entry per group writes charSqlId to group+0x14
            // ("Leader Name" display). Must ensure group leader's entry is written LAST.
            // Defer leader of each open group — write after self-inclusion.
            RRConnection deferredLeaderConn = null;
            string deferredLeaderName = null;
            uint deferredLeaderGroupId = 0;

            foreach (var kvp in _onlineUsers)
            {
                string login = kvp.Key;
                RRConnection otherConn = kvp.Value;
                if (otherConn.ConnId == conn.ConnId) continue;
                if (!otherConn.IsConnected) continue;

                string charName = GetCharacterNameForLogin(login);
                if (string.IsNullOrEmpty(charName)) continue;

                string zoneName = otherConn.CurrentZoneName ?? "unknown";
                int level = otherConn.PlayerLevel;
                uint charSqlId = otherConn.CharSqlId;
                if (charSqlId == 0) charSqlId = (uint)(otherConn.ConnId + 1);

                uint groupId = 0;
                var playerGroup = GroupManager.Instance.GetGroupForConn(otherConn.ConnId);
                if (playerGroup != null && playerGroup.IsOpen)
                {
                    groupId = playerGroup.GroupId;
                    if (!openGroups.Contains(playerGroup))
                        openGroups.Add(playerGroup);

                    // Defer if this player is the group leader AND not self
                    if (otherConn.ConnId == playerGroup.LeaderConnId)
                    {
                        deferredLeaderConn = otherConn;
                        deferredLeaderName = charName;
                        deferredLeaderGroupId = groupId;
                        continue;  // skip writing now, write after self
                    }
                }

                // Binary-verified field order (processUserListMessage@0x603C40):
                //   Field 1 → obj+0x10 (sentinel)  Field 2 → obj+0x14 (UCM+0x190 via shared tail)
                //   Field 3 → name  Field 4 → zone  Field 5 → uint16 level
                //   Field 6 → groupId  Field 7 → byte isOnline
                w.WriteUInt32(charSqlId);         // Field 1
                w.WriteUInt32(charSqlId);         // Field 2: charSqlId → obj+0x14
                w.WriteCString(charName);         // Field 3
                w.WriteCString(zoneName);         // Field 4
                w.WriteUInt16((ushort)level);     // Field 5
                w.WriteUInt32(groupId);           // Field 6: groupId
                w.WriteByte(0x01);                // Field 7
                entryCount++;
            }

            // Include self if in an open group
            {
                var selfGroup = GroupManager.Instance.GetGroupForConn(conn.ConnId);
                if (selfGroup != null && selfGroup.IsOpen)
                {
                    string selfName = GetCharacterNameForLogin(conn.LoginName ?? "");
                    uint selfSqlId = conn.CharSqlId;
                    if (selfSqlId == 0) selfSqlId = (uint)(conn.ConnId + 1);
                    if (!string.IsNullOrEmpty(selfName))
                    {
                        // If self IS leader, write self AFTER deferred leader (self goes last)
                        // If self is NOT leader, write self now, deferred leader goes last
                        if (selfGroup.LeaderConnId == conn.ConnId)
                        {
                            // Self is leader — write deferred non-leader first if any
                            // (deferredLeaderConn is null since leader=self wasn't in other loop)
                        }

                        w.WriteUInt32(selfSqlId);             // Field 1
                        w.WriteUInt32(selfSqlId);             // Field 2: charSqlId → obj+0x14
                        w.WriteCString(selfName);             // Field 3
                        w.WriteCString(conn.CurrentZoneName ?? "unknown"); // Field 4
                        w.WriteUInt16((ushort)conn.PlayerLevel); // Field 5
                        w.WriteUInt32(selfGroup.GroupId);     // Field 6: groupId
                        w.WriteByte(0x01);                    // Field 7
                        entryCount++;
                    }
                    if (!openGroups.Contains(selfGroup))
                        openGroups.Add(selfGroup);
                }
            }

            // Write deferred leader entry LAST — group+0x14 = leader charSqlId
            if (deferredLeaderConn != null)
            {
                uint lSqlId = deferredLeaderConn.CharSqlId;
                if (lSqlId == 0) lSqlId = (uint)(deferredLeaderConn.ConnId + 1);
                w.WriteUInt32(lSqlId);
                w.WriteUInt32(lSqlId);
                w.WriteCString(deferredLeaderName);
                w.WriteCString(deferredLeaderConn.CurrentZoneName ?? "unknown");
                w.WriteUInt16((ushort)deferredLeaderConn.PlayerLevel);
                w.WriteUInt32(deferredLeaderGroupId);
                w.WriteByte(0x01);
                entryCount++;
            }

            w.WriteUInt32(0x00000000);  // entry sentinel

            // Group listing section — populates Groups tab
            // Binary: Groups tab displays first member as "Leader Name"
            foreach (var group in openGroups)
            {
                w.WriteUInt32(group.GroupId);
                w.WriteByte(0x01);
                // Leader first — Groups tab shows first member as leader
                var leaderMem = group.Members.Find(gm => gm.ConnId == group.LeaderConnId);
                if (leaderMem != null)
                {
                    var lc = FindOnlineConnection(leaderMem.ConnId);
                    if (lc != null)
                    {
                        uint lSqlId = lc.CharSqlId;
                        if (lSqlId == 0) lSqlId = (uint)(lc.ConnId + 1);
                        w.WriteUInt32(lSqlId);
                    }
                }
                foreach (var m in group.Members)
                {
                    if (m.ConnId == group.LeaderConnId) continue;
                    var mc = FindOnlineConnection(m.ConnId);
                    if (mc != null)
                    {
                        uint memberSqlId = mc.CharSqlId;
                        if (memberSqlId == 0) memberSqlId = (uint)(mc.ConnId + 1);
                        w.WriteUInt32(memberSqlId);
                    }
                }
                w.WriteUInt32(0x00000000);  // member sentinel
            }
            w.WriteUInt32(0x00000000);  // group sentinel

            byte[] packet = w.ToArray();
            DRLog.Social($"Who list: {entryCount} entries, {openGroups.Count} open groups, {packet.Length} bytes");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        private RRConnection FindOnlineConnection(int connId)
        {
            foreach (var kvp in _onlineUsers)
            {
                if (kvp.Value != null && kvp.Value.ConnId == connId && kvp.Value.IsConnected)
                    return kvp.Value;
            }
            return null;
        }

        /// <summary>
        /// Type 0x0B: processSetBusy @ 0x6048D0
        /// Reads: byte → stores at [edi+0xC8], fires UI event at 0x59FEDC
        /// </summary>
        private void SendSetBusyResponse(RRConnection conn, bool busy,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var w = new LEWriter();
            w.WriteByte(0x03);
            w.WriteByte(0x0B);  // type: processSetBusy
            w.WriteByte(busy ? (byte)0x01 : (byte)0x00);
            sendCompressed(conn, 0x01, 0x0F, w.ToArray());
        }

        // ═══════════════════════════════════════════════════════════════
        // FRIEND NOTIFICATIONS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Send online/offline notification to all friends of this player.
        /// Reason 3 = online, 4 = offline (binary-verified at 0x603727/0x603682).
        /// </summary>
        private void NotifyFriendsOfStatus(string loginName, string characterName, uint reason,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var friends = GetFriends(characterName);
            // Get this player's connection for property data (level/zone)
            RRConnection playerConn = null;
            if (reason == 3)  // online — need properties
                _onlineUsers.TryGetValue(loginName, out playerConn);

            foreach (var friendCharName in friends)
            {
                // Find friend's login name from character name
                if (_charNameToLogin.TryGetValue(friendCharName, out string friendLogin))
                {
                    if (_onlineUsers.TryGetValue(friendLogin, out RRConnection friendConn))
                    {
                        if (friendConn.IsConnected)
                        {
                            // Check if the friend has us in their friends list too
                            var theirFriends = GetFriends(friendCharName);
                            if (theirFriends.Any(f => f.Equals(characterName, StringComparison.OrdinalIgnoreCase)))
                            {
                                SendRosterNotify(friendConn, characterName, reason, sendCompressed);
                                // Send level/zone properties when going ONLINE
                                if (reason == 3 && playerConn != null)
                                {
                                    SendRosterPropertyChanged(friendConn, characterName,
                                        playerConn.CurrentZoneName ?? "unknown", playerConn.PlayerLevel, sendCompressed);
                                }
                                DRLog.Social($"Notified {friendCharName} that {characterName} is {(reason == 3 ? "online" : "offline")}");
                            }
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Notify all friends when a player changes zone.
        /// Re-sends reason=3 (ONLINE) which triggers NotifyFriendsOfStatus to also send
        /// type 0x07 PropertyChanged with updated Level/World from the player's connection.
        /// </summary>
        public void NotifyFriendsZoneChange(string loginName, string characterName, string newZone,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            DRLog.Social($"NotifyFriendsZoneChange: {characterName} → {newZone}");
            NotifyFriendsOfStatus(loginName, characterName, 3, sendCompressed);  // 3 = ONLINE
        }

        /// <summary>
        /// Push updated who list to all online players.
        /// Call after zone transitions so the who tab reflects current zones.
        /// </summary>
        public void PushWhoListToAll(Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            foreach (var kvp in _onlineUsers)
            {
                if (kvp.Value != null && kvp.Value.IsConnected)
                    SendWhoList(kvp.Value, sendCompressed);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════════════════════════════

        private List<string> GetFriends(string characterName)
        {
            if (!_friends.TryGetValue(characterName, out var list))
            {
                list = new List<string>();
                _friends[characterName] = list;
            }
            return list;
        }

        private List<string> GetIgnores(string characterName)
        {
            if (!_ignores.TryGetValue(characterName, out var list))
            {
                list = new List<string>();
                _ignores[characterName] = list;
            }
            return list;
        }

        private string ReadNameFromPayload(byte[] data)
        {
            if (data == null || data.Length == 0) return null;
            try
            {
                var reader = new LEReader(data);
                return reader.ReadCString();
            }
            catch
            {
                return null;
            }
        }

        private string GetCharacterName(RRConnection conn)
        {
            // Find character name from charNameToLogin reverse lookup
            foreach (var kvp in _charNameToLogin)
            {
                if (kvp.Value.Equals(conn.LoginName, StringComparison.OrdinalIgnoreCase))
                    return kvp.Key;
            }
            return conn.LoginName ?? "Unknown";
        }

        private string GetCharacterNameForLogin(string loginName)
        {
            if (string.IsNullOrEmpty(loginName)) return loginName ?? "";
            foreach (var kvp in _charNameToLogin)
            {
                if (kvp.Value.Equals(loginName, StringComparison.OrdinalIgnoreCase))
                    return kvp.Key;
            }
            return loginName;  // fallback to loginName if char not registered yet
        }

        /// <summary>
        /// Provide online connections dictionary for Who list building.
        /// Call from server to refresh the connection mapping.
        /// </summary>
        public void UpdateConnections(Dictionary<int, RRConnection> connections)
        {
            // Rebuild from active connections
            var stale = new List<string>();
            foreach (var kvp in _onlineUsers)
            {
                if (!kvp.Value.IsConnected)
                    stale.Add(kvp.Key);
            }
            foreach (var key in stale)
                _onlineUsers.Remove(key);
        }

        // ═══════════════════════════════════════════════════════════════
        // PERSISTENCE — SQLite (social_friends / social_ignores tables)
        // ═══════════════════════════════════════════════════════════════

        private void EnsureSocialTables()
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    // V2 tables: keyed by character_name instead of account_login.
                    // Old social_friends/social_ignores tables are left untouched.
                    GameDatabase.ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS social_friends_v2 (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        character_name TEXT NOT NULL COLLATE NOCASE,
                        friend_name TEXT NOT NULL COLLATE NOCASE,
                        UNIQUE(character_name, friend_name))");
                    GameDatabase.ExecuteNonQuery(conn, @"CREATE TABLE IF NOT EXISTS social_ignores_v2 (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        character_name TEXT NOT NULL COLLATE NOCASE,
                        ignore_name TEXT NOT NULL COLLATE NOCASE,
                        UNIQUE(character_name, ignore_name))");
                }
                DRLog.Social("Social tables ensured in SQLite");
            }
            catch (Exception ex)
            {
                DRLog.Social($"Failed to create social tables: {ex.Message}");
            }
        }

        private void SaveAddFriend(string characterName, string friendName)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(conn,
                        "INSERT OR IGNORE INTO social_friends_v2 (character_name, friend_name) VALUES (@char, @friend)",
                        ("@char", characterName), ("@friend", friendName));
                }
            }
            catch (Exception ex)
            {
                DRLog.Social($"Failed to save friend: {ex.Message}");
            }
        }

        private void SaveRemoveFriend(string characterName, string friendName)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(conn,
                        "DELETE FROM social_friends_v2 WHERE character_name = @char AND friend_name = @friend",
                        ("@char", characterName), ("@friend", friendName));
                }
            }
            catch (Exception ex)
            {
                DRLog.Social($"Failed to remove friend: {ex.Message}");
            }
        }

        private void SaveAddIgnore(string characterName, string ignoreName)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(conn,
                        "INSERT OR IGNORE INTO social_ignores_v2 (character_name, ignore_name) VALUES (@char, @ignore)",
                        ("@char", characterName), ("@ignore", ignoreName));
                }
            }
            catch (Exception ex)
            {
                DRLog.Social($"Failed to save ignore: {ex.Message}");
            }
        }

        private void SaveRemoveIgnore(string characterName, string ignoreName)
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    GameDatabase.ExecuteNonQuery(conn,
                        "DELETE FROM social_ignores_v2 WHERE character_name = @char AND ignore_name = @ignore",
                        ("@char", characterName), ("@ignore", ignoreName));
                }
            }
            catch (Exception ex)
            {
                DRLog.Social($"Failed to remove ignore: {ex.Message}");
            }
        }

        private void LoadSocialData()
        {
            try
            {
                using (var conn = GameDatabase.GetConnection())
                {
                    // Load friends
                    using (var r = GameDatabase.ExecuteReader(conn, "SELECT character_name, friend_name FROM social_friends_v2"))
                    {
                        int count = 0;
                        while (r.Read())
                        {
                            string charName = GameDatabase.GetString(r, "character_name");
                            string friend = GameDatabase.GetString(r, "friend_name");
                            if (!_friends.ContainsKey(charName))
                                _friends[charName] = new List<string>();
                            if (!_friends[charName].Any(f => f.Equals(friend, StringComparison.OrdinalIgnoreCase)))
                                _friends[charName].Add(friend);
                            count++;
                        }
                        DRLog.Social($"Loaded {count} friend entries from SQLite");
                    }

                    // Load ignores
                    using (var r = GameDatabase.ExecuteReader(conn, "SELECT character_name, ignore_name FROM social_ignores_v2"))
                    {
                        int count = 0;
                        while (r.Read())
                        {
                            string charName = GameDatabase.GetString(r, "character_name");
                            string ignore = GameDatabase.GetString(r, "ignore_name");
                            if (!_ignores.ContainsKey(charName))
                                _ignores[charName] = new List<string>();
                            if (!_ignores[charName].Any(i => i.Equals(ignore, StringComparison.OrdinalIgnoreCase)))
                                _ignores[charName].Add(ignore);
                            count++;
                        }
                        DRLog.Social($"Loaded {count} ignore entries from SQLite");
                    }
                }
            }
            catch (Exception ex)
            {
                DRLog.Social($"Failed to load social data: {ex.Message}");
            }
        }
    }
}