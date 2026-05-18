// ═══════════════════════════════════════════════════════════════════════════════
// PosseManager.cs — Channel 0x0F (PosseClient) server implementation
// ═══════════════════════════════════════════════════════════════════════════════
// Binary RE verified against DungeonRunners.exe + PDB via Ghidra (2026-05-16):
//
// CHANNEL: PosseClient registers at TChannelManager slot 15 (0x0F).
//   Evidence: PosseClient::start @ VA 0x00610610 has PUSH 0xf @ 0x00610676
//   immediately before CALL TChannelManager<>::createChannel @ 0x0061067F.
//   Same pattern as UserManagerClient (slot 3) and GroupClient (slot 11).
//
// SERVER → CLIENT message dispatch (jump table @ 0x00611F60). Full map captured 2026-05-16
// via Ghidra dump (WORK\file.md) — every case now has a PDB-resolved handler:
//   0x00  processGetInfo                  fn @ 0x00612a20
//   0x01  processInvite                   fn @ 0x00612aa0
//   0x02  processKick                     fn @ 0x006133c0
//   0x03  processPromote                  fn @ 0x006138d0
//   0x04  processDemote                   fn @ 0x00613f90
//   0x05  processGetRoster                fn @ 0x00614650
//   0x06  processChangeLeader             fn @ 0x00614920
//   0x07  processLeaderChanged            fn @ 0x00614ce0
//   0x08  processDisband                  fn @ 0x00614fd0  ← calls ClearCachedPosse
//   0x09  (default / drop — JA past table)
//   0x0A  (default / drop — JA past table)
//   0x0B  processJoin                     fn @ 0x006152d0
//   0x0C  processLeave                    fn @ 0x006157a0  ← calls RemovePosseMember
//   0x0D  processUpdateCachedPosse        fn @ 0x00615AD0
//   0x0E  processConnectionNotification   fn @ 0x00615B80
//   0x0F  processPropertyChanged          fn @ 0x00616050
//   0x10  processMemberPropertyChanged    fn @ 0x006162c0
//   0x11  processInvitationNotification   fn @ 0x006166b0
//   0x12  processInviteRequestResult      fn @ 0x006131b0
//   0x13  processInviteResponseResult     fn @ 0x006167e0
//   0x14  processCreate                   fn @ 0x00615C50  ← wired
//
// CLIENT-SIDE CACHED-POSSE CLEAR PATH (Ghidra-confirmed):
//   PosseClient::ClearCachedPosse @ 0x00612930
//     - sets [this+0xa4] = sentinel (clears posse_id)
//     - clears bit 0 of [this+0x128]  (the "have cached posse info" bit)
//     - calls CachedPosseInfo::Clear
//   PosseClient::RemovePosseMember @ 0x00612960 (param: CharSQLID = 4 bytes)
//     - if param == [this+0xa4] (=== "self is the leaver") → identical clear path as Disband
//     - else → just removes that member from the local CachedPosseInfo
//
// So the *correct* way to wipe a player's posse panel on the client is to send either
// processDisband (opcode 0x08) — for an actual disband — or processLeave (opcode 0x0C)
// with kicked_char == self — for a voluntary leave or being kicked. Wire formats for
// 0x08 and 0x0C are still pending the next Ghidra slice (see POSSE_GHIDRA_QUESTIONS).
//
// processConnectionNotification wire format (verified @ 0x00615B80–0x00615C4B):
//   [byte connected]  — 0x00 = "Posses are currently unavailable", nonzero = enabled.
//   On nonzero: sets bit 1 of [this+0x128] and fires DFCEventLite(0xF0006),
//   which is the UI event that flips the Posse panel state.
//
// "Posses are currently unavailable." (string @ VA 0x008AEF78) is checked at the top
// of EVERY action handler: processInvite, processKick, processPromote, processDemote,
// processChangeLeader, processDisband, processJoin, processLeave, processCreate.
// So this single connection push unlocks the entire posse action surface in one shot.
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using UnityEngine;
using DungeonRunners.Utilities;
using DungeonRunners.Data;
using DungeonRunners.Database;

namespace DungeonRunners.Networking
{
    public class PosseManager
    {
        private static PosseManager _instance;
        public static PosseManager Instance => _instance ??= new PosseManager();

        // Pending invites: invitee_char_id → posse_id. Set in HandleInboundInvite when
        // SendInvitationNotification fires; consumed by HandleInboundAcceptInvite (inbound 0x10).
        private readonly System.Collections.Generic.Dictionary<uint, uint> _pendingInvites
            = new System.Collections.Generic.Dictionary<uint, uint>();


        public const byte CHANNEL = 0x0F;
        // Outbound opcodes (server → client). Full table from Ghidra dispatch dump.
        private const byte MSG_PROCESS_GET_INFO                 = 0x00;
        private const byte MSG_PROCESS_INVITE                   = 0x01;
        private const byte MSG_PROCESS_KICK                     = 0x02;
        private const byte MSG_PROCESS_PROMOTE                  = 0x03;
        private const byte MSG_PROCESS_DEMOTE                   = 0x04;
        private const byte MSG_PROCESS_GET_ROSTER               = 0x05;
        private const byte MSG_PROCESS_CHANGE_LEADER            = 0x06;
        private const byte MSG_PROCESS_LEADER_CHANGED           = 0x07;
        private const byte MSG_PROCESS_DISBAND                  = 0x08;
        private const byte MSG_PROCESS_JOIN                     = 0x0B;
        private const byte MSG_PROCESS_LEAVE                    = 0x0C;
        private const byte MSG_PROCESS_UPDATE_CACHED_POSSE      = 0x0D;
        private const byte MSG_PROCESS_CONNECTION_NOTIFICATION  = 0x0E;
        private const byte MSG_PROCESS_PROPERTY_CHANGED         = 0x0F;
        private const byte MSG_PROCESS_MEMBER_PROPERTY_CHANGED  = 0x10;
        private const byte MSG_PROCESS_INVITATION_NOTIFICATION  = 0x11;
        private const byte MSG_PROCESS_INVITE_REQUEST_RESULT    = 0x12;
        private const byte MSG_PROCESS_INVITE_RESPONSE_RESULT   = 0x13;
        private const byte MSG_PROCESS_CREATE                   = 0x14;
        // Inbound opcodes (client → server). Note: the byte values can OVERLAP with outbound
        // opcodes — the dispatch is direction-specific (different code path).
        private const byte MSG_CHAT_COMMAND        = 0x01;  // payload = null-terminated text after "/posse "
        private const byte MSG_INBOUND_INVITE      = 0x03;  // PosseClient::InvitePlayerToPosse → [CString player_name]
        private const byte MSG_INBOUND_KICK        = 0x04;  // onKickPosseMemberConfirmation     → [CString member_name]
        private const byte MSG_INBOUND_PROMOTE     = 0x05;  // PromotePosseMember                → [CString member_name]
        private const byte MSG_INBOUND_DEMOTE      = 0x06;  // DemotePosseMember                 → [CString member_name]
        private const byte MSG_INBOUND_SET_LEADER  = 0x08;  // onChangePosseLeaderConfirmation   → [CString new_leader_name]
        private const byte MSG_INBOUND_DISBAND     = 0x09;  // onDisbandPosseConfirmation        → empty
        private const byte MSG_INBOUND_ACCEPT_INVITE = 0x10; // invitee accepts a pending invite → empty payload (server tracks pending via _pendingInvites)
        private const byte MSG_INBOUND_DECLINE_INVITE = 0x11; // invitee declines a pending invite → 1-byte payload (e.g. 0xF2 = status code observed in test)
        private const byte MSG_INBOUND_LEAVE       = 0x0D;  // onLeavePosseConfirmation          → empty
        private const byte MSG_INBOUND_CREATE      = 0x0E;  // panel/Tad "Create Posse" form    → [CString posse_name]
        private const byte MSG_INBOUND_SET_PROP    = 0x0F;  // PosseInfoEditPanel submit         → [CString "id;flag;value;"]
        private const string EMPTY_STRING_SENTINEL = "EmptyString_Placeholder";

        // processCreate status codes (from processCreate switch in client EXE):
        private const uint STATUS_SUCCESS         = 0x00;
        private const uint STATUS_NAME_COLLISION  = 0x02;
        private const uint STATUS_ALREADY_MEMBER  = 0x06;
        private const uint STATUS_MUST_WAIT       = 0x18;
        private const uint STATUS_UNAVAILABLE     = 0x63;
        private const uint STATUS_NOT_ENOUGH_GOLD = 0xec;
        private const uint STATUS_NAME_FILTER     = 0xee;

        // GlobalKnobs.gc:105-107 — authored native values.
        private const int MIN_LEVEL_TO_CREATE      = 15;
        private const uint GOLD_COST_TO_CREATE     = 1000000;
        // PosseInvitationTimeout = 30s, used for invitations (Step C), not create cooldown.
        // The retail post-leave cooldown duration is not in plaintext (probably inside
        // game.pkg). 30s is a placeholder until Baron or Ghidra reveals the authored value.
        private const int POST_LEAVE_COOLDOWN_SECS = 30;

        public PosseManager() { }

        public void HandleMessage(RRConnection conn, byte messageType, byte[] data,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, UnityGameServer server)
        {
            string hex = (data != null && data.Length > 0)
                ? BitConverter.ToString(data, 0, Math.Min(48, data.Length))
                : "EMPTY";
            Debug.LogError($"[POSSE] inbound type=0x{messageType:X2} dataLen={data?.Length ?? 0} hex={hex}");

            switch (messageType)
            {
                case MSG_CHAT_COMMAND:
                    HandleChatCommand(conn, data, sendCompressed, server);
                    break;
                case MSG_INBOUND_INVITE:
                    HandleInboundInvite(conn, ReadFirstCString(data), sendCompressed, server);
                    break;
                case MSG_INBOUND_KICK:
                    HandleInboundKick(conn, ReadFirstCString(data), sendCompressed, server);
                    break;
                case MSG_INBOUND_PROMOTE:
                    HandleInboundPromote(conn, ReadFirstCString(data), sendCompressed, server);
                    break;
                case MSG_INBOUND_DEMOTE:
                    HandleInboundDemote(conn, ReadFirstCString(data), sendCompressed, server);
                    break;
                case MSG_INBOUND_SET_LEADER:
                    HandleInboundSetLeader(conn, ReadFirstCString(data), sendCompressed, server);
                    break;
                case MSG_INBOUND_DISBAND:
                    HandleDisband(conn, sendCompressed, server);
                    break;
                case MSG_INBOUND_ACCEPT_INVITE:
                    HandleInboundAcceptInvite(conn, sendCompressed, server);
                    break;
                case MSG_INBOUND_DECLINE_INVITE:
                    HandleInboundDeclineInvite(conn, sendCompressed, server);
                    break;
                case MSG_INBOUND_LEAVE:
                    HandleLeave(conn, sendCompressed, server);
                    break;
                case MSG_INBOUND_CREATE:
                    HandleInboundCreate(conn, data, sendCompressed, server);
                    break;
                case MSG_INBOUND_SET_PROP:
                    HandleInboundSetProperty(conn, data, sendCompressed, server);
                    break;
                default:
                    Debug.LogError($"[POSSE] no handler for inbound type=0x{messageType:X2}");
                    break;
            }
        }

        // Inbound 0x0E: client's "Create Posse" form (Tad's dialog or the right-side panel)
        // submits with payload = null-terminated posse name. Same downstream validation as the
        // /posse create chat verb.
        private void HandleInboundCreate(RRConnection conn, byte[] data,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, UnityGameServer server)
        {
            string name = "";
            if (data != null && data.Length > 0)
            {
                try { name = new LEReader(data).ReadCString(); }
                catch { Debug.LogError("[POSSE] inbound-create payload not null-terminated"); }
            }
            Debug.LogError($"[POSSE] inbound-create form submit name='{name}'");
            HandleCreate(conn, name, sendCompressed, server);
        }

        private static string ReadFirstCString(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            try { return new LEReader(data).ReadCString(); }
            catch { return ""; }
        }

        // Inbound 0x03 — InvitePlayerToPosse(target_name). Look up the target character, and if
        // they're online, push processInvitationNotification (outbound 0x11) to their connection.
        // Persistence of pending invites is in-memory only for now; PosseInvitationTimeout=30s.
        private void HandleInboundInvite(RRConnection conn, string targetName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, UnityGameServer server)
        {
            Debug.LogError($"[POSSE-INVITE] {conn.LoginName} invites '{targetName}'");
            var (posse, savedChar) = RequireRank(conn, server, 8);   // rank ≥ 8 can invite
            if (posse == null) return;
            if (string.IsNullOrWhiteSpace(targetName))
            {
                server.SendSystemMessage(conn, "Usage: invite a player by name.");
                return;
            }

            var targetCharRow = CharacterRepository.GetCharacterByName(conn.LoginName, targetName);
            if (targetCharRow == null)
            {
                server.SendSystemMessage(conn, $"No character named '{targetName}'.");
                return;
            }
            if (targetCharRow.posseId != 0)
            {
                server.SendSystemMessage(conn, $"'{targetName}' is already in a posse.");
                return;
            }

            RRConnection targetConn = null;
            foreach (var c in server.GetConnectedMemberConnsForPosse(0)) { } // no-op; use general iteration
            foreach (var memberConn in server.AllConnectedConnections())
            {
                var gc = server.GetSelectedCharacter(memberConn.LoginName);
                if (gc != null && gc.Id == targetCharRow.id) { targetConn = memberConn; break; }
            }
            if (targetConn == null)
            {
                server.SendSystemMessage(conn, $"'{targetName}' is not online.");
                return;
            }

            SendInvitationNotification(targetConn, savedChar.name, posse.Name,
                (c, dest, mt, data) => server.SendSocialViaAuthPublic(c, dest, mt, data));
            // Remember the pending invite so we can resolve it on inbound 0x10 (Accept).
            _pendingInvites[targetCharRow.id] = posse.Id;
            Debug.LogError($"[POSSE-INVITE] tracking pending invite: char={targetCharRow.id} ('{targetName}') → posse={posse.Id} ('{posse.Name}')");
            server.SendSystemMessage(conn, $"Invited '{targetName}' to '{posse.Name}'.");
        }

        // Inbound 0x10 — invitee clicks Accept on the InvitationNotification popup.
        // Wire format: empty payload. Server identifies the inviter/posse via _pendingInvites
        // (populated by HandleInboundInvite). On accept: update DB, broadcast CachedPosseFull
        // to all members (including the new one), notify the original inviter.
        private void HandleInboundAcceptInvite(RRConnection conn,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, UnityGameServer server)
        {
            var savedChar = LookupSavedChar(conn, server);
            if (savedChar == null)
            {
                Debug.LogError($"[POSSE-ACCEPT] no selected character for {conn.LoginName}");
                return;
            }
            if (!_pendingInvites.TryGetValue(savedChar.id, out uint posseId))
            {
                Debug.LogError($"[POSSE-ACCEPT] {savedChar.name} accepted but no pending invite found");
                return;
            }
            _pendingInvites.Remove(savedChar.id);

            var posse = PosseRepository.GetPosse(posseId);
            if (posse == null)
            {
                Debug.LogError($"[POSSE-ACCEPT] pending posse {posseId} no longer exists for {savedChar.name}");
                server.SendSystemMessage(conn, "That posse no longer exists.");
                return;
            }
            if (savedChar.posseId != 0)
            {
                Debug.LogError($"[POSSE-ACCEPT] {savedChar.name} already in posse {savedChar.posseId}");
                server.SendSystemMessage(conn, "You are already in a posse.");
                return;
            }

            // Add to the posse: UPDATE characters SET posse_id=posseId, posse_rank_id=1.
            // New joiners start at the bottom of the 1..10 scale; founder must promote.
            using (var dbConn = GameDatabase.GetConnection())
            {
                GameDatabase.ExecuteNonQuery(dbConn,
                    "UPDATE characters SET posse_id = @pid, posse_rank_id = 1 WHERE id = @cid",
                    ("@pid", (int)posseId), ("@cid", (int)savedChar.id));
            }
            savedChar.posseId = posseId;
            savedChar.posseName = posse.Name;
            savedChar.posseRankId = 1;
            CharacterRepository.SaveCharacter(savedChar);
            Debug.LogError($"[POSSE-ACCEPT] {savedChar.name} joined '{posse.Name}' (id={posseId})");

            // Rebuild member list and broadcast CachedPosseFull to every connected member.
            var members = new System.Collections.Generic.List<(uint charId, string name, bool isFounder)>();
            using (var dbConn = GameDatabase.GetConnection())
            using (var r = GameDatabase.ExecuteReader(dbConn,
                "SELECT id, name FROM characters WHERE posse_id = @pid ORDER BY id",
                ("@pid", (int)posseId)))
            {
                while (r.Read())
                {
                    uint cid = (uint)r.GetInt32(0);
                    string cname = r.GetString(1);
                    members.Add((cid, cname, cid == posse.FounderCharacterId));
                }
            }

            foreach (var memberConn in server.AllConnectedConnections())
            {
                var gc = server.GetSelectedCharacter(memberConn.LoginName);
                if (gc == null) continue;
                bool inThisPosse = members.Exists(m => m.charId == gc.Id);
                if (!inThisPosse) continue;
                SendCachedPosseFull(memberConn, gc.Id, posse, members,
                    (c, dest, mt, data) => server.SendSocialViaAuthPublic(c, dest, mt, data), server);
            }

            server.SendSystemMessage(conn, $"You joined '{posse.Name}'.");
        }

        // Inbound 0x11 — invitee clicks Decline on the InvitationNotification popup.
        // Wire format: single byte (observed value 0xF2). Server's job is just to clear the
        // pending invite so it doesn't dangle, and notify the inviter via a chat line.
        private void HandleInboundDeclineInvite(RRConnection conn,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, UnityGameServer server)
        {
            var savedChar = LookupSavedChar(conn, server);
            if (savedChar == null) return;
            if (!_pendingInvites.TryGetValue(savedChar.id, out uint posseId))
            {
                Debug.LogError($"[POSSE-DECLINE] {savedChar.name} declined but no pending invite tracked");
                return;
            }
            _pendingInvites.Remove(savedChar.id);
            var posse = PosseRepository.GetPosse(posseId);
            string posseName = posse?.Name ?? $"posse {posseId}";
            Debug.LogError($"[POSSE-DECLINE] {savedChar.name} declined invite to '{posseName}'");

            // Notify the original founder if they're online via processInviteRequestResult
            // (status 0xF2 = "X declined your invitation."). Falls back to nothing if founder
            // is offline — they'll see the decline implicitly because no roster update arrives.
            if (posse != null)
            {
                foreach (var memberConn in server.AllConnectedConnections())
                {
                    var gc = server.GetSelectedCharacter(memberConn.LoginName);
                    if (gc != null && gc.Id == posse.FounderCharacterId)
                    {
                        SendInviteRequestResult(memberConn, INVITE_RESULT_DECLINED,
                            posseId, savedChar.name, server);
                        break;
                    }
                }
            }
        }

        // Outbound 0x12 (processInviteRequestResult) — Ghidra-verified @ VA 0x006131b0.
        // Wire format: [u32 status_code][u32 posse_id][CString target_name]
        // The handler reads status, posse_id, and (via readString) a String. It compares
        // posse_id to [PosseClient+0xa4] (recipient's posse_id) — chat is suppressed unless
        // they match. Then dispatches on (status - 0xEF) into a 4-case switch:
        //   0xEF → "Posse invite failed! %s is busy."
        //   0xF0 → "Posse invite for %s timed out!"
        //   0xF1 → "Posse invite failed! %s not logged on."
        //   0xF2 → "%s declined your invitation."
        // Any other status falls through to default → no chat. The success case ("X accepted")
        // is NOT handled here; the inviter learns via the CachedPosseFull broadcast on accept.
        private const uint INVITE_RESULT_TARGET_BUSY     = 0xEFu;
        private const uint INVITE_RESULT_TIMED_OUT       = 0xF0u;
        private const uint INVITE_RESULT_TARGET_OFFLINE  = 0xF1u;
        private const uint INVITE_RESULT_DECLINED        = 0xF2u;

        private void SendInviteRequestResult(RRConnection conn, uint statusCode,
            uint posseId, string targetName, UnityGameServer server)
        {
            var w = new LEWriter();
            w.WriteByte(CHANNEL);
            w.WriteByte(MSG_PROCESS_INVITE_REQUEST_RESULT);
            w.WriteUInt32(statusCode);
            w.WriteUInt32(posseId);
            w.WriteCString(targetName ?? "");
            byte[] packet = w.ToArray();
            Debug.LogError($"[POSSE] >> SENDING InviteRequestResult status=0x{statusCode:X2} posseId={posseId} target='{targetName}' hex={BitConverter.ToString(packet)}");
            server.SendSocialViaAuthPublic(conn, 0x01, 0x0F, packet);
        }

        // Inbound 0x04 — kick a named member from the posse.
        private void HandleInboundKick(RRConnection conn, string targetName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, UnityGameServer server)
        {
            var (posse, savedChar) = RequireRank(conn, server, 8);   // rank ≥ 8 can kick
            if (posse == null) return;
            if (string.IsNullOrWhiteSpace(targetName) || string.Equals(targetName, savedChar.name, StringComparison.OrdinalIgnoreCase))
            {
                server.SendSystemMessage(conn, "You can't kick yourself — use /posse disband instead.");
                return;
            }
            var target = CharacterRepository.GetCharacterByName(conn.LoginName, targetName);
            if (target == null || target.posseId != posse.Id)
            {
                server.SendSystemMessage(conn, $"'{targetName}' is not a member of your posse.");
                return;
            }
            // Can't kick the founder; can't kick anyone of equal-or-higher rank than yourself
            // (founder bypass: founder can kick anyone non-founder).
            if (target.id == posse.FounderCharacterId)
            {
                server.SendSystemMessage(conn, "You cannot kick the posse founder.");
                return;
            }
            if (posse.FounderCharacterId != savedChar.id && target.posseRankId >= savedChar.posseRankId)
            {
                server.SendSystemMessage(conn, $"You cannot kick '{target.name}' — their rank is equal or higher than yours.");
                return;
            }
            int cooldownExpires = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + POST_LEAVE_COOLDOWN_SECS;
            PosseRepository.ClearCharacterMembership(target.id, cooldownExpires);
            Debug.LogError($"[POSSE-KICK] {savedChar.name} kicked {target.name} from '{posse.Name}'");

            // Notify the victim if online — processKick (outbound 0x02) so their client's
            // RemovePosseMember(self) fires ClearCachedPosse and renders "You were kicked".
            foreach (var memberConn in server.AllConnectedConnections())
            {
                var gc = server.GetSelectedCharacter(memberConn.LoginName);
                if (gc != null && gc.Id == target.id)
                {
                    var w = new LEWriter();
                    w.WriteByte(CHANNEL);
                    w.WriteByte(MSG_PROCESS_KICK);
                    // Wire format (Ghidra-verified):
                    //   uint32 #1 — compared against [PosseClient+0xa4] = recipient's CharSQLID.
                    //               For the victim, that's target.id (their own CharSQLID).
                    //   uint32 #2 — passed to RemovePosseMember (= kicked CharSQLID).
                    //               When #1==#2, RemovePosseMember treats it as "self is leaving"
                    //               and fires ClearCachedPosse (wipes [+0xa4], clears the
                    //               have-cached-info bit). That's how the victim's UI refreshes
                    //               without a relog.
                    //   uint32 #3 — status code (0 = success).
                    w.WriteUInt32(target.id);          // recipient's own CharSQLID
                    w.WriteUInt32(target.id);          // kicked CharSQLID (matches → ClearCachedPosse)
                    w.WriteUInt32(STATUS_SUCCESS);
                    server.SendSocialViaAuthPublic(memberConn, 0x01, 0x0F, w.ToArray());
                    Debug.LogError($"[POSSE-KICK] Notified victim {target.name} via processKick");
                    break;
                }
            }

            // Refresh remaining members' panels.
            BroadcastCachedPosseFull(posse, server);
        }

        // Inbound 0x05 — promote a member: set their posse_rank_id to founder rank.
        private void HandleInboundPromote(RRConnection conn, string targetName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, UnityGameServer server)
        {
            // Only the founder (rank 10) may promote.
            var (posse, savedChar) = RequireFounder(conn, server);
            if (posse == null) return;
            var target = CharacterRepository.GetCharacterByName(conn.LoginName, targetName);
            if (target == null || target.posseId != posse.Id)
            {
                server.SendSystemMessage(conn, $"'{targetName}' is not a member of your posse.");
                return;
            }
            if (target.id == posse.FounderCharacterId)
            {
                server.SendSystemMessage(conn, "You cannot promote the posse founder.");
                return;
            }
            int newRank = target.posseRankId + 1;
            if (newRank > 9)   // cap at 9; rank 10 is reserved for the founder
            {
                server.SendSystemMessage(conn, $"'{target.name}' is already at the highest non-founder rank (9). Use /posse setleader to transfer founder.");
                return;
            }
            using (var dbConn = GameDatabase.GetConnection())
            {
                GameDatabase.ExecuteNonQuery(dbConn,
                    "UPDATE characters SET posse_rank_id = @r WHERE id = @cid",
                    ("@r", newRank), ("@cid", (int)target.id));
            }
            Debug.LogError($"[POSSE-PROMOTE] {savedChar.name} promoted {target.name} from rank {target.posseRankId} → {newRank} in '{posse.Name}'");
            BroadcastProcessPromote(posse, savedChar.id, target.id, (uint)newRank, server);
            BroadcastCachedPosseFull(posse, server);
        }

        // Inbound 0x06 — demote a member: drop their posse_rank_id by 1 (min 1).
        private void HandleInboundDemote(RRConnection conn, string targetName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, UnityGameServer server)
        {
            // Only the founder (rank 10) may demote.
            var (posse, savedChar) = RequireFounder(conn, server);
            if (posse == null) return;
            var target = CharacterRepository.GetCharacterByName(conn.LoginName, targetName);
            if (target == null || target.posseId != posse.Id)
            {
                server.SendSystemMessage(conn, $"'{targetName}' is not a member of your posse.");
                return;
            }
            if (target.id == posse.FounderCharacterId)
            {
                server.SendSystemMessage(conn, "You cannot demote the posse founder.");
                return;
            }
            int newRank = target.posseRankId - 1;
            if (newRank < 1)
            {
                server.SendSystemMessage(conn, $"'{target.name}' is already at the lowest rank (1).");
                return;
            }
            using (var dbConn = GameDatabase.GetConnection())
            {
                GameDatabase.ExecuteNonQuery(dbConn,
                    "UPDATE characters SET posse_rank_id = @r WHERE id = @cid",
                    ("@r", newRank), ("@cid", (int)target.id));
            }
            Debug.LogError($"[POSSE-DEMOTE] {savedChar.name} demoted {target.name} from rank {target.posseRankId} → {newRank} in '{posse.Name}'");
            BroadcastProcessDemote(posse, savedChar.id, target.id, (uint)newRank, server);
            BroadcastCachedPosseFull(posse, server);
        }

        // ProcessPromote outbound (0x03) — chat-notify all connected posse members.
        // Wire format (Ghidra-verified @ 0x006138d0):
        //   [0x0F][0x03][u32 status][u32 promoter_char_id][u32 promoted_char_id][u32 new_rank]
        // Client behaviour on status=0:
        //   - SetMemberRankLevel(promoted_char_id, new_rank) → updates cached member's rank_id
        //   - If promoter_char_id == local player's [DAT_00932f8c]: shows "<PROMOTED> was promoted to Rank %u"
        //   - Else: shows "<PROMOTER> promoted <PROMOTED> to Rank %u"
        private void BroadcastProcessPromote(PosseRecord posse, uint promoterCharId, uint promotedCharId, uint newRank, UnityGameServer server)
        {
            foreach (var memberConn in server.AllConnectedConnections())
            {
                var gc = server.GetSelectedCharacter(memberConn.LoginName);
                if (gc == null) continue;
                var mc = CharacterRepository.GetCharacter(gc.Id);
                if (mc == null || mc.posseId != posse.Id) continue;
                var w = new LEWriter();
                w.WriteByte(CHANNEL);
                w.WriteByte(MSG_PROCESS_PROMOTE);
                w.WriteUInt32(STATUS_SUCCESS);
                w.WriteUInt32(promoterCharId);
                w.WriteUInt32(promotedCharId);
                w.WriteUInt32(newRank);
                server.SendSocialViaAuthPublic(memberConn, 0x01, 0x0F, w.ToArray());
            }
            Debug.LogError($"[POSSE-PROMOTE] Notified posse '{posse.Name}' members (promoter={promoterCharId}, promoted={promotedCharId}, newRank={newRank})");
        }

        // ProcessDemote outbound (0x04) — same shape as ProcessPromote, just opcode 0x04 and a lower rank.
        // Wire format (Ghidra-verified @ 0x00613f90, structurally parallel to processPromote):
        //   [0x0F][0x04][u32 status][u32 demoter_char_id][u32 demoted_char_id][u32 new_rank]
        private void BroadcastProcessDemote(PosseRecord posse, uint demoterCharId, uint demotedCharId, uint newRank, UnityGameServer server)
        {
            foreach (var memberConn in server.AllConnectedConnections())
            {
                var gc = server.GetSelectedCharacter(memberConn.LoginName);
                if (gc == null) continue;
                var mc = CharacterRepository.GetCharacter(gc.Id);
                if (mc == null || mc.posseId != posse.Id) continue;
                var w = new LEWriter();
                w.WriteByte(CHANNEL);
                w.WriteByte(MSG_PROCESS_DEMOTE);
                w.WriteUInt32(STATUS_SUCCESS);
                w.WriteUInt32(demoterCharId);
                w.WriteUInt32(demotedCharId);
                w.WriteUInt32(newRank);
                server.SendSocialViaAuthPublic(memberConn, 0x01, 0x0F, w.ToArray());
            }
            Debug.LogError($"[POSSE-DEMOTE] Notified posse '{posse.Name}' members (demoter={demoterCharId}, demoted={demotedCharId}, newRank={newRank})");
        }

        // Inbound 0x08 — transfer founder role to another member.
        private void HandleInboundSetLeader(RRConnection conn, string targetName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, UnityGameServer server)
        {
            var (posse, savedChar) = RequireFounder(conn, server);
            if (posse == null) return;
            var target = CharacterRepository.GetCharacterByName(conn.LoginName, targetName);
            if (target == null || target.posseId != posse.Id)
            {
                server.SendSystemMessage(conn, $"'{targetName}' is not a member of your posse.");
                return;
            }
            if (target.id == savedChar.id)
            {
                server.SendSystemMessage(conn, "You are already the founder.");
                return;
            }
            uint oldLeaderId = savedChar.id;
            uint newLeaderId = target.id;
            using (var dbConn = GameDatabase.GetConnection())
            {
                // Transfer founder role + swap ranks: new leader gets rank 10, old leader drops
                // to rank 9 (Ghidra-implied: processLeaderChanged calls SetMemberRankLevel on
                // the old leader, demoting them out of the founder slot).
                GameDatabase.ExecuteNonQuery(dbConn,
                    "UPDATE posses SET founder_character_id = @nfid WHERE id = @pid",
                    ("@nfid", (int)newLeaderId), ("@pid", (int)posse.Id));
                GameDatabase.ExecuteNonQuery(dbConn,
                    "UPDATE characters SET posse_rank_id = 10 WHERE id = @cid",
                    ("@cid", (int)newLeaderId));
                GameDatabase.ExecuteNonQuery(dbConn,
                    "UPDATE characters SET posse_rank_id = 9 WHERE id = @cid",
                    ("@cid", (int)oldLeaderId));
            }
            posse.FounderCharacterId = newLeaderId;
            savedChar.posseRankId = 9;
            target.posseRankId = 10;
            Debug.LogError($"[POSSE-LEADER] {savedChar.name} transferred leader of '{posse.Name}' to {target.name}");
            BroadcastProcessChangeLeader(posse, oldLeaderId, newLeaderId, server);
            BroadcastCachedPosseFull(posse, server);
        }

        // Outbound 0x06 (processChangeLeader) — Ghidra-verified wire format
        // @ VA 0x00614920: reads [u32 status]. On status==0 delegates to processLeaderChanged
        // @ VA 0x00614ce0 which reads 4 more uint32s:
        //   F1 → old leader char_id (key for "X is no longer leader" chat via map lookup at [PosseClient+0xd4])
        //   F2 → nonzero flag (slot [ESP+0x10] at 614da9; must be != 0 for old-leader chat to fire)
        //   F3 → new leader char_id (key for "X is now leader" chat via second map lookup)
        //   F4 → nonzero flag (slot [ESP+0x1c] at 614ed2; must be != 0 for new-leader chat to fire)
        // Total payload after channel+opcode: 0x14 bytes (status + 4 uint32s).
        // Status codes other than 0 trigger error chat lines (e.g. "Failed to set Leader.",
        // "You are not the founder...", "Posses are currently unavailable.") — we never send
        // those from the server path because we already rejected the action via SendSystemMessage.
        private void BroadcastProcessChangeLeader(PosseRecord posse, uint oldLeaderId, uint newLeaderId,
            UnityGameServer server)
        {
            foreach (var memberConn in server.GetConnectedMemberConnsForPosse(posse.Id))
            {
                var w = new LEWriter();
                w.WriteByte(CHANNEL);
                w.WriteByte(MSG_PROCESS_CHANGE_LEADER);
                w.WriteUInt32(STATUS_SUCCESS);   // 0 → delegate to processLeaderChanged
                w.WriteUInt32(oldLeaderId);      // F1
                w.WriteUInt32(1u);               // F2 (nonzero flag)
                w.WriteUInt32(newLeaderId);      // F3
                w.WriteUInt32(1u);               // F4 (nonzero flag)
                server.SendSocialViaAuthPublic(memberConn, 0x01, 0x0F, w.ToArray());
            }
            Debug.LogError($"[POSSE-LEADER] Broadcast ChangeLeader to posse '{posse.Name}' members (old={oldLeaderId}, new={newLeaderId})");
        }

        // Inbound 0x0F: client's "Edit Posse Philosophy/MOTD" form submits a semicolon-separated
        // CString: "<property_id>;<flag>;<new_value>;". Empty input is wired with the literal
        // sentinel "EmptyString_Placeholder" (field placeholder text sent verbatim by the client).
        // The dialog waits for a server ACK + cached-posse refresh to close cleanly — without one
        // the dialog hangs and the client eventually faults.
        private void HandleInboundSetProperty(RRConnection conn, byte[] data,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, UnityGameServer server)
        {
            string payload = "";
            if (data != null && data.Length > 0)
            {
                try { payload = new LEReader(data).ReadCString(); }
                catch { Debug.LogError("[POSSE] set-property payload not null-terminated"); return; }
            }
            Debug.LogError($"[POSSE] inbound set-property payload='{payload}'");

            // Parse client format. Reverse-engineered from PosseInfoPanel::OnEditFinished:
            //   String::Format("%u;%u;%s;", 1 /* constant */, propertyId, value)
            //   → parts[0] = "1" (always), parts[1] = propertyId, parts[2] = value
            // propertyId 0 = Posse Philosophy (panel+0x190), 1 = MOTD (panel+0x18c).
            string[] parts = payload.Split(';');
            uint propertyId = 0;
            if (parts.Length >= 2) uint.TryParse(parts[1], out propertyId);
            string newValue = parts.Length >= 3 ? parts[2] : "";

            // Rank ≥ 8 — matches the Ghidra-verified PosseInfoEditPanel::refresh gate that
            // controls whether the Edit MOTD/Philosophy panel even opens.
            var (posse, savedChar) = RequireRank(conn, server, 8);
            if (posse == null) return;

            // DB-persist: sentinel means user submitted without typing → store empty.
            string storedValue = newValue == EMPTY_STRING_SENTINEL ? "" : newValue;
            bool ok = propertyId == 1
                ? PosseRepository.SetMotd(posse.Id, storedValue)
                : PosseRepository.SetDescription(posse.Id, storedValue);
            if (!ok)
            {
                server.SendSystemMessage(conn, "Failed to update posse property.");
                return;
            }
            if (propertyId == 1) posse.Motd = storedValue; else posse.Description = storedValue;
            Debug.LogError($"[POSSE-PROP] {savedChar.name} set property id={propertyId} on posse {posse.Id}: payload='{newValue}' stored='{storedValue}'");

            // CRITICAL: do NOT send anything posse-related back to the SUBMITTER. After
            // OnEditFinished runs the PosseInfoEditPanel has already destroyed itself, but
            // it's still registered as a DFCEvent listener until cleanup completes — any
            // outbound 0x0F or 0x0D (both fire UI events the destroyed panel handles in its
            // doEvent switch) crashes via use-after-free on the dispatcher's callback list.
            // The submitter already saw their own change applied locally via SetPropertyLabel
            // before DestroyEditPanel ran, so they don't need a server confirmation.
            //
            // Broadcast to OTHER online members of the posse so they see the change.
            foreach (var memberConn in server.GetConnectedMemberConnsForPosse(posse.Id))
            {
                if (memberConn == conn) continue; // skip submitter — see comment above
                var gcObj = server.GetSelectedCharacter(memberConn.LoginName);
                if (gcObj == null) continue;
                var memberList = new System.Collections.Generic.List<(uint, string, bool)>();
                using (var dbConn = GameDatabase.GetConnection())
                using (var r = GameDatabase.ExecuteReader(dbConn,
                    "SELECT id, name FROM characters WHERE posse_id = @pid ORDER BY id",
                    ("@pid", (int)posse.Id)))
                {
                    while (r.Read())
                    {
                        uint cid = (uint)r.GetInt32(0);
                        string cname = r.GetString(1);
                        memberList.Add((cid, cname, cid == posse.FounderCharacterId));
                    }
                }
                SendCachedPosseFull(memberConn, gcObj.Id, posse, memberList,
                    (c, dest, mt, d) => server.SendSocialViaAuthPublic(c, dest, mt, d), server);
            }
        }

        // Outbound 0x0F (processPropertyChanged) — Ghidra-verified wire format @ VA 0x00616050:
        //   [byte 0x0F][byte 0x0F][uint32 property_id][CString new_value]
        // The handler reads property_id, formats it as "%u" decimal to use as a std::map key
        // (so property_id=1 → key "1"), then stores the value. If new_value equals the literal
        // "EmptyString_Placeholder" sentinel, the property is cleared.
        // Fires UI event 0xF0007 — this is what closes the PosseInfoEditPanel dialog.
        public void SendPropertyChanged(RRConnection conn, uint propertyId, string newValue,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var w = new LEWriter();
            w.WriteByte(CHANNEL);
            w.WriteByte(MSG_PROCESS_PROPERTY_CHANGED);
            w.WriteUInt32(propertyId);
            w.WriteCString(newValue ?? "");
            byte[] packet = w.ToArray();
            Debug.LogError($"[POSSE] >> SENDING PropertyChanged id={propertyId} value='{newValue}' hex={BitConverter.ToString(packet)}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        // Posse-channel opcode 0x01 = chat-command relay. Payload is a null-terminated
        // CString of everything the player typed after "/posse ". Empirically observed:
        //   "/posse create TestPosse" → "create TestPosse\0"
        //   "/posse help"             → "help\0"
        private void HandleChatCommand(RRConnection conn, byte[] data,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, UnityGameServer server)
        {
            if (data == null || data.Length == 0) return;
            string commandLine;
            try { commandLine = new LEReader(data).ReadCString(); }
            catch { Debug.LogError("[POSSE] chat-cmd payload not null-terminated"); return; }

            int spaceIdx = commandLine.IndexOf(' ');
            string verb = spaceIdx < 0 ? commandLine : commandLine.Substring(0, spaceIdx);
            string args = spaceIdx < 0 ? "" : commandLine.Substring(spaceIdx + 1);
            Debug.LogError($"[POSSE] chat-cmd verb='{verb}' args='{args}'");

            switch (verb.ToLowerInvariant())
            {
                case "create":
                    HandleCreate(conn, args, sendCompressed, server);
                    break;
                case "leave":
                    HandleLeave(conn, sendCompressed, server);
                    break;
                case "disband":
                    HandleDisband(conn, sendCompressed, server);
                    break;
                case "rename":
                    HandleRename(conn, args, sendCompressed, server);
                    break;
                case "motd":
                    HandleSetMotd(conn, args, sendCompressed, server);
                    break;
                case "desc":
                case "description":
                    HandleSetDescription(conn, args, sendCompressed, server);
                    break;
                case "info":
                case "status":
                    HandleInfo(conn, server);
                    break;
                case "members":
                case "roster":
                case "who":
                    HandleMembers(conn, server);
                    break;
                case "help":
                case "?":
                    HandleHelp(conn, server);
                    break;
                case "invite":
                    HandleInboundInvite(conn, args, sendCompressed, server);
                    break;
                case "kick":
                    HandleInboundKick(conn, args, sendCompressed, server);
                    break;
                case "promote":
                    HandleInboundPromote(conn, args, sendCompressed, server);
                    break;
                case "demote":
                    HandleInboundDemote(conn, args, sendCompressed, server);
                    break;
                case "setleader":
                case "leader":
                    HandleInboundSetLeader(conn, args, sendCompressed, server);
                    break;
                default:
                    Debug.LogError($"[POSSE] unhandled chat verb '{verb}'");
                    server.SendSystemMessage(conn, $"Unknown /posse command '{verb}'. Try /posse help.");
                    break;
            }
        }

        private void HandleCreate(RRConnection conn, string rawName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, UnityGameServer server)
        {
            string name = (rawName ?? "").Trim();

            // Player must be in-world and selected.
            var gcObj = server.GetSelectedCharacter(conn.LoginName);
            if (gcObj == null)
            {
                Debug.LogError($"[POSSE-CREATE] {conn.LoginName} has no selected character");
                SendCreateResponse(conn, STATUS_UNAVAILABLE, name, sendCompressed);
                return;
            }

            var savedChar = CharacterRepository.GetCharacter(gcObj.Id);
            if (savedChar == null)
            {
                Debug.LogError($"[POSSE-CREATE] character id={gcObj.Id} not found in DB");
                SendCreateResponse(conn, STATUS_UNAVAILABLE, name, sendCompressed);
                return;
            }

            // Already in a posse.
            if (savedChar.posseId != 0)
            {
                Debug.LogError($"[POSSE-CREATE] {savedChar.name} already in posse id={savedChar.posseId}");
                SendCreateResponse(conn, STATUS_ALREADY_MEMBER, name, sendCompressed);
                return;
            }

            // Name validation (length + simple character filter — the authored word filter
            // dictionary is inside encrypted game.pkg, so we do a permissive baseline).
            if (name.Length < 2 || name.Length > 32 || HasIllegalNameChars(name))
            {
                Debug.LogError($"[POSSE-CREATE] name '{name}' failed length/char filter");
                SendCreateResponse(conn, STATUS_NAME_FILTER, name, sendCompressed);
                return;
            }

            // Level gate (GlobalKnobs.MinLevelToCreatePosse = 15). The client doesn't have
            // a per-status string for "too low level", so we use UNAVAILABLE and log it.
            if (savedChar.level < MIN_LEVEL_TO_CREATE)
            {
                Debug.LogError($"[POSSE-CREATE] {savedChar.name} level {savedChar.level} < {MIN_LEVEL_TO_CREATE}");
                SendCreateResponse(conn, STATUS_UNAVAILABLE, name, sendCompressed);
                return;
            }

            // Cooldown after a previous join/leave.
            int nowUnix = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (savedChar.posseJoinCooldown > nowUnix)
            {
                Debug.LogError($"[POSSE-CREATE] {savedChar.name} cooldown until {savedChar.posseJoinCooldown}, now {nowUnix}");
                SendCreateResponse(conn, STATUS_MUST_WAIT, name, sendCompressed);
                return;
            }

            // Gold gate (GlobalKnobs.GoldCostToCreatePosse = 1,000,000).
            if (savedChar.gold < GOLD_COST_TO_CREATE)
            {
                Debug.LogError($"[POSSE-CREATE] {savedChar.name} gold {savedChar.gold} < {GOLD_COST_TO_CREATE}");
                SendCreateResponse(conn, STATUS_NOT_ENOUGH_GOLD, name, sendCompressed);
                return;
            }

            // Name collision (case-insensitive via posses.name COLLATE NOCASE).
            if (PosseRepository.PosseNameExists(name))
            {
                Debug.LogError($"[POSSE-CREATE] name '{name}' already taken");
                SendCreateResponse(conn, STATUS_NAME_COLLISION, name, sendCompressed);
                return;
            }

            // Persist: create posse row, link character.
            var posse = PosseRepository.CreatePosse(name, savedChar.id, GOLD_COST_TO_CREATE);
            if (posse == null)
            {
                Debug.LogError($"[POSSE-CREATE] DB insert failed for '{name}'");
                SendCreateResponse(conn, STATUS_UNAVAILABLE, name, sendCompressed);
                return;
            }

            savedChar.gold -= GOLD_COST_TO_CREATE;
            savedChar.posseId = posse.Id;
            savedChar.posseName = posse.Name;
            savedChar.posseRankId = 10;   // founder always rank 10
            CharacterRepository.SaveCharacter(savedChar);
            // SaveCharacter doesn't currently write posse_rank_id; do it explicitly.
            using (var dbConn = GameDatabase.GetConnection())
            {
                GameDatabase.ExecuteNonQuery(dbConn,
                    "UPDATE characters SET posse_rank_id = 10 WHERE id = @cid",
                    ("@cid", (int)savedChar.id));
            }
            Debug.LogError($"[POSSE-CREATE] ✓ {savedChar.name} founded '{posse.Name}' id={posse.Id} gold {savedChar.gold + GOLD_COST_TO_CREATE} → {savedChar.gold}");

            // Update client gold display (same wire shape as MerchantManager.HandleBuyItem /
            // respec gold removal — verified to play the coin jingle).
            SendGoldDeduction(conn, GOLD_COST_TO_CREATE, sendCompressed, server);

            // Push the full cached posse state (basic info + 2-rank table + founder as member)
            // so the panel UI populates immediately. Then announce success — the client's
            // processCreate status switch fires the "You successfully created the posse X" line.
            var members = new System.Collections.Generic.List<(uint, string, bool)>
            {
                (savedChar.id, savedChar.name, true),
            };
            SendCachedPosseFull(conn, savedChar.id, posse, members, sendCompressed, server);
            SendCreateResponse(conn, STATUS_SUCCESS, posse.Name, sendCompressed);
        }

        private void HandleLeave(RRConnection conn,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, UnityGameServer server)
        {
            var savedChar = LookupSavedChar(conn, server);
            if (savedChar == null)
            {
                server.SendSystemMessage(conn, "You are not in a posse.");
                return;
            }
            if (savedChar.posseId == 0)
            {
                server.SendSystemMessage(conn, "You are not in a posse.");
                return;
            }

            uint leftPosseId = savedChar.posseId;
            string leftPosseName = savedChar.posseName ?? "";
            int cooldownExpires = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + POST_LEAVE_COOLDOWN_SECS;

            // If the leaver is the last member, the posse is gone; otherwise just unlink them.
            int remainingMembers = PosseRepository.MemberCount(leftPosseId) - 1;
            if (remainingMembers <= 0)
            {
                PosseRepository.DisbandPosse(leftPosseId, cooldownExpires);
                Debug.LogError($"[POSSE-LEAVE] {savedChar.name} was last member of '{leftPosseName}' (id={leftPosseId}) — posse dissolved");
            }
            else
            {
                PosseRepository.ClearCharacterMembership(savedChar.id, cooldownExpires);
                Debug.LogError($"[POSSE-LEAVE] {savedChar.name} left '{leftPosseName}' (id={leftPosseId}, {remainingMembers} remain)");
            }

            // processLeave(0x0C): when wire's leaver_char_id == local player, the client
            // internally calls RemovePosseMember → ClearCachedPosse, which clears [+0xa4],
            // clears bit 0 of [+0x128] (have-info), wipes CachedPosseInfo, and fires UI
            // event 0xF0003. DO NOT follow with SendCachedPosseUpdate(0,0) — that re-sets
            // bit 0 of [+0x128] via `OR [EDI+0x128], 0x1`, which Tad's CheckPosseRegistryOption
            // reads as "Already in a Posse!" even though our payload is empty.
            SendProcessLeave(conn, savedChar.id, STATUS_SUCCESS, leftPosseName, sendCompressed);
            // No extra SendSystemMessage — the client renders its own "You left the posse X" chat line.
        }

        private void HandleDisband(RRConnection conn,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, UnityGameServer server)
        {
            var savedChar = LookupSavedChar(conn, server);
            if (savedChar == null || savedChar.posseId == 0)
            {
                server.SendSystemMessage(conn, "You are not in a posse.");
                return;
            }
            var posse = PosseRepository.GetPosse(savedChar.posseId);
            if (posse == null)
            {
                server.SendSystemMessage(conn, "Your posse no longer exists.");
                return;
            }
            if (posse.FounderCharacterId != savedChar.id)
            {
                server.SendSystemMessage(conn, "Only the posse founder can disband. Try /posse leave instead.");
                return;
            }

            int cooldownExpires = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + POST_LEAVE_COOLDOWN_SECS;
            string posseName = posse.Name;
            var affected = PosseRepository.DisbandPosse(savedChar.posseId, cooldownExpires);
            Debug.LogError($"[POSSE-DISBAND] {savedChar.name} disbanded '{posseName}' (id={posse.Id}, {affected.Count} members affected)");

            // processDisband(0x08) triggers ClearCachedPosse on the client + fires UI event
            // 0xF0002 + renders "The posse X was disbanded". Same caveat as HandleLeave: do
            // NOT follow with SendCachedPosseUpdate(0,0), it re-sets the have-info bit that
            // Tad's CheckPosseRegistryOption reads as "Already in a Posse!".
            SendProcessDisband(conn, STATUS_SUCCESS, posseName, sendCompressed);
            // No extra SendSystemMessage — the client renders its own "The posse X was disbanded" chat line.
        }

        private void HandleInfo(RRConnection conn, UnityGameServer server)
        {
            var savedChar = LookupSavedChar(conn, server);
            if (savedChar == null || savedChar.posseId == 0)
            {
                server.SendSystemMessage(conn, "You are not in a posse. Use /posse create <name> to start one.");
                return;
            }
            var posse = PosseRepository.GetPosse(savedChar.posseId);
            int count = PosseRepository.MemberCount(savedChar.posseId);
            string foundedAt = posse?.FoundedAt ?? "?";
            string founderTag = posse != null && posse.FounderCharacterId == savedChar.id ? " (you founded it)" : "";
            server.SendSystemMessage(conn, $"Posse '{savedChar.posseName}' — {count} member(s), founded {foundedAt}{founderTag}.");
            if (posse != null)
            {
                if (!string.IsNullOrEmpty(posse.Description))
                    server.SendSystemMessage(conn, $"Description: {posse.Description}");
                if (!string.IsNullOrEmpty(posse.Motd))
                    server.SendSystemMessage(conn, $"MOTD: {posse.Motd}");
            }
        }

        private void HandleMembers(RRConnection conn, UnityGameServer server)
        {
            var savedChar = LookupSavedChar(conn, server);
            if (savedChar == null || savedChar.posseId == 0)
            {
                server.SendSystemMessage(conn, "You are not in a posse.");
                return;
            }
            var names = PosseRepository.MemberNames(savedChar.posseId);
            if (names.Count == 0)
            {
                server.SendSystemMessage(conn, $"Posse '{savedChar.posseName}' has no members? (DB inconsistency)");
                return;
            }
            server.SendSystemMessage(conn, $"Posse '{savedChar.posseName}' members ({names.Count}): {string.Join(", ", names)}");
        }

        private void HandleHelp(RRConnection conn, UnityGameServer server)
        {
            server.SendSystemMessage(conn, "Posse commands:");
            server.SendSystemMessage(conn, "  /posse create <name>      — found a posse (level 15+, 1,000,000 gold)");
            server.SendSystemMessage(conn, "  /posse info               — show your current posse");
            server.SendSystemMessage(conn, "  /posse members            — list members");
            server.SendSystemMessage(conn, "  /posse rename <name>      — founder-only: rename");
            server.SendSystemMessage(conn, "  /posse motd <text>        — founder-only: set MOTD (blank to clear)");
            server.SendSystemMessage(conn, "  /posse desc <text>        — founder-only: set description");
            server.SendSystemMessage(conn, "  /posse leave              — leave your posse");
            server.SendSystemMessage(conn, "  /posse disband            — founder-only: delete the posse");
            server.SendSystemMessage(conn, "  /posse invite <name>      — rank 8+: invite a player");
            server.SendSystemMessage(conn, "  /posse kick <name>        — rank 8+: kick a lower-ranked member");
            server.SendSystemMessage(conn, "  /posse promote <name>     — founder-only: bump a member's rank by 1 (max 9)");
            server.SendSystemMessage(conn, "  /posse demote <name>      — founder-only: drop a member's rank by 1 (min 1)");
            server.SendSystemMessage(conn, "  /posse setleader <name>   — founder-only: transfer founder role");
            server.SendSystemMessage(conn, "(Ranks: 1 = new joiner, 10 = founder. Rank 8+ can edit MOTD/Description and invite/kick.)");
        }

        private void HandleRename(RRConnection conn, string rawName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, UnityGameServer server)
        {
            string newName = (rawName ?? "").Trim();
            var (posse, savedChar) = RequireFounder(conn, server);
            if (posse == null) return;
            if (newName.Length < 2 || newName.Length > 32 || HasIllegalNameChars(newName))
            {
                server.SendSystemMessage(conn, "Posse name failed validation.");
                return;
            }
            if (PosseRepository.PosseNameExists(newName))
            {
                server.SendSystemMessage(conn, $"A posse named '{newName}' already exists.");
                return;
            }
            string oldName = posse.Name;
            if (!PosseRepository.Rename(posse.Id, newName))
            {
                server.SendSystemMessage(conn, "Rename failed.");
                return;
            }
            Debug.LogError($"[POSSE-RENAME] {savedChar.name} renamed posse {posse.Id}: '{oldName}' → '{newName}'");
            posse.Name = newName;
            BroadcastCachedPosseFull(posse, server);
            server.SendSystemMessage(conn, $"Posse renamed: '{oldName}' → '{newName}'.");
        }

        private void HandleSetMotd(RRConnection conn, string motd,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, UnityGameServer server)
        {
            // MOTD edit: rank ≥ 8 (matches the Ghidra-verified PosseInfoEditPanel::refresh gate).
            var (posse, savedChar) = RequireRank(conn, server, 8);
            if (posse == null) return;
            motd = (motd ?? "").Trim();
            if (motd.Length > 256) { server.SendSystemMessage(conn, "MOTD must be 256 characters or fewer."); return; }
            if (!PosseRepository.SetMotd(posse.Id, motd))
            {
                server.SendSystemMessage(conn, "Failed to set MOTD.");
                return;
            }
            Debug.LogError($"[POSSE-MOTD] {savedChar.name} set MOTD on posse {posse.Id}: '{motd}'");
            posse.Motd = motd;
            BroadcastCachedPosseFull(posse, server);
            server.SendSystemMessage(conn, string.IsNullOrEmpty(motd) ? "MOTD cleared." : $"MOTD set: {motd}");
        }

        private void HandleSetDescription(RRConnection conn, string desc,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, UnityGameServer server)
        {
            // Description edit: rank ≥ 8 (same panel-permission tier as MOTD).
            var (posse, savedChar) = RequireRank(conn, server, 8);
            if (posse == null) return;
            desc = (desc ?? "").Trim();
            if (desc.Length > 512) { server.SendSystemMessage(conn, "Description must be 512 characters or fewer."); return; }
            if (!PosseRepository.SetDescription(posse.Id, desc))
            {
                server.SendSystemMessage(conn, "Failed to set description.");
                return;
            }
            Debug.LogError($"[POSSE-DESC] {savedChar.name} set description on posse {posse.Id}: '{desc}'");
            posse.Description = desc;
            BroadcastCachedPosseFull(posse, server);
            server.SendSystemMessage(conn, string.IsNullOrEmpty(desc) ? "Description cleared." : $"Description set: {desc}");
        }

        // Founder-gate + lookup shared by rename/motd/desc/promote/demote/kick.
        private (PosseRecord, SavedCharacter) RequireFounder(RRConnection conn, UnityGameServer server)
        {
            var savedChar = LookupSavedChar(conn, server);
            if (savedChar == null || savedChar.posseId == 0)
            {
                server.SendSystemMessage(conn, "You are not in a posse.");
                return (null, null);
            }
            var posse = PosseRepository.GetPosse(savedChar.posseId);
            if (posse == null)
            {
                server.SendSystemMessage(conn, "Your posse record was not found.");
                return (null, null);
            }
            if (posse.FounderCharacterId != savedChar.id)
            {
                server.SendSystemMessage(conn, "Only the posse founder can do that.");
                return (null, null);
            }
            return (posse, savedChar);
        }

        // RequireRank: gate by posse rank rather than founder identity.
        // Returns (null, null) and sends the appropriate error if the caller is not in a posse
        // or doesn't meet the rank floor. The founder (rank 10) always passes.
        private (PosseRecord, SavedCharacter) RequireRank(RRConnection conn, UnityGameServer server, int minRank)
        {
            var savedChar = LookupSavedChar(conn, server);
            if (savedChar == null || savedChar.posseId == 0)
            {
                server.SendSystemMessage(conn, "You are not in a posse.");
                return (null, null);
            }
            var posse = PosseRepository.GetPosse(savedChar.posseId);
            if (posse == null)
            {
                server.SendSystemMessage(conn, "Your posse record was not found.");
                return (null, null);
            }
            // Founder always passes; otherwise compare stored posse_rank_id against the floor.
            if (posse.FounderCharacterId != savedChar.id && savedChar.posseRankId < minRank)
            {
                server.SendSystemMessage(conn, $"Your posse rank ({savedChar.posseRankId}) is too low — requires rank {minRank} or higher.");
                return (null, null);
            }
            return (posse, savedChar);
        }

        // Public real-time presence helper. Call when any posse member's observable state
        // changes (login, logout, zone change, level up) — broadcasts the fresh CachedPosseFull
        // to all currently-online members of that posse so their rosters update live.
        // Safe to call for non-posse characters (no-op).
        public void NotifyMemberStateChange(uint charId, UnityGameServer server)
        {
            if (server == null) return;
            var sc = CharacterRepository.GetCharacter(charId);
            if (sc == null || sc.posseId == 0) return;
            var posse = PosseRepository.GetPosse(sc.posseId);
            if (posse == null) return;
            BroadcastCachedPosseFull(posse, server);
        }

        // Send updated CachedPosseFull to every currently-online member of the posse, so
        // rename/promote/demote/etc. take effect immediately on every connected member's panel.
        // Each member gets a packet with THEIR own CharSQLID at +0xa4 (for self-leave detection),
        // so we resolve their char_id per-connection.
        private void BroadcastCachedPosseFull(PosseRecord posse, UnityGameServer server)
        {
            var members = new System.Collections.Generic.List<(uint, string, bool)>();
            using (var dbConn = GameDatabase.GetConnection())
            using (var r = GameDatabase.ExecuteReader(dbConn,
                "SELECT id, name FROM characters WHERE posse_id = @pid ORDER BY id",
                ("@pid", (int)posse.Id)))
            {
                while (r.Read())
                {
                    uint cid = (uint)r.GetInt32(0);
                    string cname = r.GetString(1);
                    members.Add((cid, cname, cid == posse.FounderCharacterId));
                }
            }
            foreach (var memberConn in server.GetConnectedMemberConnsForPosse(posse.Id))
            {
                var gcObj = server.GetSelectedCharacter(memberConn.LoginName);
                if (gcObj == null) continue;
                SendCachedPosseFull(memberConn, gcObj.Id, posse, members, (c, dest, mt, data) =>
                    server.SendSocialViaAuthPublic(c, dest, mt, data), server);
            }
        }

        private SavedCharacter LookupSavedChar(RRConnection conn, UnityGameServer server)
        {
            var gcObj = server.GetSelectedCharacter(conn.LoginName);
            if (gcObj == null) return null;
            return CharacterRepository.GetCharacter(gcObj.Id);
        }

        private static bool HasIllegalNameChars(string s)
        {
            foreach (char c in s)
            {
                if (c < 0x20) return true;       // control chars / null
                if (c == '<' || c == '>') return true;  // HTML-like brackets
                if (c == '"') return true;
            }
            return false;
        }

        // Mirrors the gold-removal wire packet used by MerchantManager.HandleBuyItem and
        // the respec path: ComponentUpdate(0x35) + AddCurrency(0x20) with a negative delta.
        private void SendGoldDeduction(RRConnection conn, uint amount,
            Action<RRConnection, byte, byte, byte[]> sendCompressed, UnityGameServer server)
        {
            if (conn.UnitContainerId == 0) { Debug.LogError("[POSSE-CREATE] skip gold packet (UnitContainerId=0)"); return; }
            var w = new LEWriter();
            w.WriteByte(0x07);                       // BeginStream
            w.WriteByte(0x35);                       // ComponentUpdate
            w.WriteUInt16(conn.UnitContainerId);     // player UnitContainer
            w.WriteByte(0x20);                       // AddCurrency (fires 0x138A coin jingle)
            w.WriteInt32(-(int)amount);
            w.WriteByte(0x00);                       // CurrencySource
            w.WriteUInt32(0x00000000);               // entityHandle (required by 0x20 parser)
            w.WriteByte(0x01);                       // notifyFlag
            server.WritePlayerEntitySynch(conn, w);
            w.WriteByte(0x06);                       // EndStream
            sendCompressed(conn, 0x01, 0x0F, w.ToArray());
            Debug.LogError($"[POSSE-CREATE] Sent RemoveCurrency {amount}");
        }

        // processLeave wire format (Ghidra @ VA 0x006157a0):
        //   [byte 0x0F][byte 0x0C][uint32 status][uint32 leaver_char_id][CString posse_name]
        // The first 4-byte stream read lands at [ESP+0x18] post-return, which the switch at
        // CMP 0/8/0x63 reads from — that's the STATUS. The second 4-byte read lands at
        // [ESP+0x24] (=EBP in the success path), then PUSHed into RemovePosseMember(CharSQLID).
        // Order matters: getting it backwards makes the client see status=1 = "Unknown error".
        // Client behaviour by status:
        //   0     "success"     → RemovePosseMember(leaver_char_id). If the leaver IS the local
        //                        player, that internally calls ClearCachedPosse (wipes [+0xa4],
        //                        clears bit 0 of [+0x128]). UI event 0xF0003. Chat:
        //                          "You left the posse %s"   (if self)
        //                          "%s left the posse %s"    (member name from cache, posse name from wire)
        //   8     "no longer in posse" → "Leave posse failed! Player is no longer in posse?"
        //   0x63  "unavailable" → "Posses are currently unavailable"
        //   else  unknown error → "Unknown error (%d) leaving posse"
        public void SendProcessLeave(RRConnection conn, uint leaverCharId, uint status, string posseName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var w = new LEWriter();
            w.WriteByte(CHANNEL);
            w.WriteByte(MSG_PROCESS_LEAVE);
            w.WriteUInt32(status);             // ← status FIRST (was reversed before)
            w.WriteUInt32(leaverCharId);       // ← then the char id
            w.WriteCString(posseName ?? "");
            byte[] packet = w.ToArray();
            Debug.LogError($"[POSSE] >> SENDING ProcessLeave status=0x{status:X2} leaver={leaverCharId} name='{posseName}' hex={BitConverter.ToString(packet)}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        // processDisband wire format (Ghidra @ VA 0x00614fd0):
        //   [byte 0x0F][byte 0x08][uint32 status][CString posse_name]
        // Client behaviour by status:
        //   0     "success"     → calls ClearCachedPosse (wipes [+0xa4], clears bit 0 of [+0x128]).
        //                        UI event 0xF0002. Chat: "The posse %s was disbanded".
        //   8     "not founder"   → "Disband posse %s Failed! You are not the founder."
        //   0x0d  "not found"     → "Failed to disband %s! Posse not found."
        //   0x13  "no posse"      → "Failed to disband %s! You don't have a posse."
        //   0x63  "unavailable"   → "Posses are currently unavailable"
        public void SendProcessDisband(RRConnection conn, uint status, string posseName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var w = new LEWriter();
            w.WriteByte(CHANNEL);
            w.WriteByte(MSG_PROCESS_DISBAND);
            w.WriteUInt32(status);
            w.WriteCString(posseName ?? "");
            byte[] packet = w.ToArray();
            Debug.LogError($"[POSSE] >> SENDING ProcessDisband status=0x{status:X2} name='{posseName}' hex={BitConverter.ToString(packet)}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        // processInvitationNotification wire format (Ghidra @ VA 0x006166b0):
        //   [byte 0x0F][byte 0x11][CString inviter_name][CString posse_name]
        // Client behaviour: if already in invite-state (bit 0 of [+0x120]) → auto-declines with
        // status 0xef. Otherwise stores inviter_name at [+0x11c], posse_name at [+0x118], sets
        // bit 0 of [+0x120], fires UI event 0xF000F (invite popup dialog).
        public void SendInvitationNotification(RRConnection conn, string inviterName, string posseName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var w = new LEWriter();
            w.WriteByte(CHANNEL);
            w.WriteByte(MSG_PROCESS_INVITATION_NOTIFICATION);
            w.WriteCString(inviterName ?? "");
            w.WriteCString(posseName ?? "");
            byte[] packet = w.ToArray();
            Debug.LogError($"[POSSE] >> SENDING InvitationNotification inviter='{inviterName}' posse='{posseName}' hex={BitConverter.ToString(packet)}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        // processCreate wire format (Ghidra @ VA 0x00615C50):
        //   [byte 0x0F][byte 0x14][uint32 status][CString posse_name]
        // Client switch on status (each case formats a chat line + UI event 0xF0001):
        //   0      success         "You successfully created the posse %s!"
        //   2      name collision  "Create posse failed! Another posse already exists with the name %s."
        //   6      already member  "Create posse failed! You are already a member of a posse."
        //   0x18   must wait       "Create posse failed! You must wait before joining/creating a posse."
        //   0x1c   already member  "Create posse failed! You are already a member of a posse."
        //   0x63   unavailable     "Posses are currently unavailable."
        //   0xec   not enough gold "Create posse failed! You don't have enough gold."
        //   0xee   name filter     "Create posse failed! Posse name did not pass the filter!"
        public void SendCreateResponse(RRConnection conn, uint status, string posseName,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var w = new LEWriter();
            w.WriteByte(CHANNEL);              // 0x0F
            w.WriteByte(MSG_PROCESS_CREATE);   // 0x14
            w.WriteUInt32(status);
            w.WriteCString(posseName ?? "");
            byte[] packet = w.ToArray();
            Debug.LogError($"[POSSE] >> SENDING CreateResponse status=0x{status:X2} name='{posseName}' hex={BitConverter.ToString(packet)}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        /// <summary>
        /// Push processConnectionNotification(connected) to the client. Flips the Posse
        /// tab between "Posses are currently unavailable" and enabled state.
        /// </summary>
        public void SendConnectionNotification(RRConnection conn, bool connected,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var w = new LEWriter();
            w.WriteByte(CHANNEL);                                  // 0x0F → PosseClient
            w.WriteByte(MSG_PROCESS_CONNECTION_NOTIFICATION);      // 0x0E
            w.WriteByte((byte)(connected ? 0x01 : 0x00));
            byte[] packet = w.ToArray();
            Debug.LogError($"[POSSE] >> SENDING ConnectionNotification connected={connected} hex={BitConverter.ToString(packet)}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        // processUpdateCachedPosse wire format (Ghidra @ VA 0x00615AD0):
        //   [byte channel 0x0F][byte opcode 0x0D][uint32 local_player_char_sql_id]
        //   [CachedPosseInfo::Deserialize body]
        //
        // CRITICAL: the first uint32 is stored at [PosseClient+0xa4] and is the LOCAL PLAYER's
        // CharSQLID, NOT the posse_id. processLeave/processKick use it via
        //   CMP [PosseClient+0xa4], leaver_char_id   → if equal, "self left/kicked" path that
        //                                              calls ClearCachedPosse (wipes cache).
        // If we put posse_id here instead, self-events route through the "other player" path
        // and the cache never clears, leaving Tad's "Already in a Posse" stuck on. The actual
        // posse identity lives in CachedPosseInfo+0/+4 (section 1 id1) — that's the posse SQL id.
        //
        // CachedPosseInfo::Deserialize wire format (Ghidra @ VA 0x00616F90):
        //   [byte dirty_flags]  ← ONE byte, not four!
        //     if (dirty_flags & 0x02): SECTION 1 — basic info
        //     if (dirty_flags & 0x04): SECTION 2 — ranks
        //     if (dirty_flags & 0x08): SECTION 3 — members
        //     if (dirty_flags & 0x10): SECTION 4 — properties
        //     if (dirty_flags & 0x20): SECTION 5 — achievements
        //
        //   SECTION 1 (basic info, bit 0x02):
        //     [uint64 id1]      → CachedPosseInfo+0x00/+0x04
        //     [uint32 int_val]  → CachedPosseInfo+0x08    (semantics: member count? founder id?)
        //     [CString name]    → CachedPosseInfo+0x0c (String::assign)
        //     [uint64 id2]      → CachedPosseInfo+0x10/+0x14
        //     [byte flag]       → CachedPosseInfo+0x18 (SETNZ — stored as 0 or 1)
        //
        //   SECTION 2 (ranks, bit 0x04): ClearRankData first, then —
        //     [uint32 rank_count]
        //     per rank:
        //       [uint32 rank_id]
        //       [CString rank_name]
        //       [uint64 permissions]
        //
        //   SECTION 3 (members, bit 0x08):
        //     [uint32 member_count]
        //     per member:
        //       [byte present_flag]      — 0 = skip this slot, non-zero = AddPosseMember
        //       if non-zero: [AddPosseMember body — see below]
        //
        //   CachedPosseInfo::AddPosseMember (Ghidra @ VA 0x006176F0):
        //     [uint64 char_sql_id]   ← member's CharSQLID (low 4 bytes match the wire id used in
        //                              processKick/processLeave; full 8 bytes for the std::map key)
        //     [uint32 rank_id]       ← maps to one of the ranks pushed in section 2
        //     [CString member_name]
        //     [uint64 permissions]
        //     [uint32 property_count]
        //     per property:
        //       [CString key]
        //       [CString value]
        //
        //   SECTION 4 (properties, bit 0x10):
        //     [uint32 prop_count]
        //     per property:
        //       [CString key]
        //       [CString value]
        //
        //   SECTION 5 (achievements/players map, bit 0x20): more complex nested map —
        //   not wired for now (achievements aren't surfaced in the panel UI for our use).
        //
        // After Deserialize, processUpdateCachedPosse sets bit 0 of [PosseClient+0x128]
        // ("have cached posse info") and fires UI event 0xF0005.

        private const byte DIRTY_BASIC      = 0x02;
        private const byte DIRTY_RANKS      = 0x04;
        private const byte DIRTY_MEMBERS    = 0x08;
        private const byte DIRTY_PROPERTIES = 0x10;
        private const byte DIRTY_ACHIEVEMENTS = 0x20;

        // Minimal-payload UpdateCachedPosse with no sections. Currently unused: we never send
        // this for the "no-posse" state, because it always sets bit 0 of [PosseClient+0x128]
        // ("have cached posse info"), which Tad's CheckPosseRegistryOption reads as
        // "Already in a Posse!". For posse-less players, only ConnectionNotification is sent;
        // for posse-having players, SendCachedPosseFull replaces this.
        public void SendCachedPosseUpdate(RRConnection conn, uint localPlayerCharId, byte dirtyFlags,
            Action<RRConnection, byte, byte, byte[]> sendCompressed)
        {
            var w = new LEWriter();
            w.WriteByte(CHANNEL);
            w.WriteByte(MSG_PROCESS_UPDATE_CACHED_POSSE);
            w.WriteUInt32(localPlayerCharId);  // ← +0xa4 = local player CharSQLID, NOT posse id
            w.WriteByte(dirtyFlags);
            byte[] packet = w.ToArray();
            Debug.LogError($"[POSSE] >> SENDING UpdateCachedPosse charId={localPlayerCharId} flags=0x{dirtyFlags:X2} hex={BitConverter.ToString(packet)}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }

        // Sends a full cached-posse state push: section 1 (basic) + section 2 (default 2-rank
        // table) + section 3 (one member entry per character with posse_id=this). This is the
        // canonical "your posse looks like this" packet used on create-success and on login
        // restore. Rank list is synthesized in-memory since we don't persist authored ranks.
        public void SendCachedPosseFull(RRConnection conn, uint localPlayerCharId, PosseRecord posse,
            System.Collections.Generic.List<(uint charId, string name, bool isFounder)> members,
            Action<RRConnection, byte, byte, byte[]> sendCompressed,
            UnityGameServer server = null)
        {
            // Pre-compute online set for accurate per-member "online" property.
            // When server is null (legacy callsites), every member is marked online — same as before this change.
            var onlineCharIds = new System.Collections.Generic.HashSet<uint>();
            if (server != null)
            {
                foreach (var memberConn in server.AllConnectedConnections())
                {
                    var gc = server.GetSelectedCharacter(memberConn.LoginName);
                    if (gc != null) onlineCharIds.Add(gc.Id);
                }
            }
            var w = new LEWriter();
            w.WriteByte(CHANNEL);
            w.WriteByte(MSG_PROCESS_UPDATE_CACHED_POSSE);
            // +0xa4 = local recipient's CharSQLID (NOT posse id — see header comment).
            // Critical for the client's self-leave/kick detection in processLeave/processKick.
            w.WriteUInt32(localPlayerCharId);

            // Section 4 properties: client's PosseInfoPanel::SetAllFields iterates the map and
            // calls `_strtol(key, NULL, 10)` to convert each key to an integer — that integer
            // selects which UI field to update via SetPropertyLabel:
            //   key "0" → Philosophy field (panel+0x190)
            //   key "1" → MOTD field (panel+0x18c)
            // ALWAYS include both keys (even empty) so the Edit panel can find them on click —
            // skipping empties leaves the map without these keys and crashes the EditPanel
            // construction when it tries to read the current value.
            var props = new System.Collections.Generic.List<(string key, string val)>();
            props.Add(("0", posse.Description ?? ""));   // PossePropertyId::Philosophy
            props.Add(("1", posse.Motd ?? ""));           // PossePropertyId::MOTD

            byte flags = (byte)(DIRTY_BASIC | DIRTY_RANKS | DIRTY_MEMBERS);
            if (props.Count > 0) flags |= DIRTY_PROPERTIES;
            w.WriteByte(flags);

            // SECTION 1 — basic info
            w.WriteUInt64(posse.Id);                          // id1 = posse SQL id (zero-extended)
            w.WriteUInt32((uint)members.Count);               // int_val: best guess = member count
            w.WriteCString(posse.Name ?? "");
            w.WriteUInt64(posse.FounderCharacterId);          // id2 = founder char id
            w.WriteByte(0x01);                                // flag = active

            // SECTION 2 — ranks. Native posse uses a 1..10 numeric scale (no rank names; panel
            // displays the number). Per-rank permissions are a uint64 bitmask the client reads
            // for button-visibility gating. Two Ghidra-verified bits matter:
            //   bit 0x10000 (bit 16) — PosseInfoPanel::SetButtons @ 0x004b25d5: enables the
            //                            Edit MOTD / Edit Description button display. If unset
            //                            for a member's rank, the buttons stay hidden.
            //   bit 0x20000 (bit 17) — PosseInfoEditPanel::refresh @ 0x004b1444: must be set
            //                            for the EditPanel to survive construction (otherwise
            //                            refresh calls hide → crash).
            // Both bits must be on for a member to safely click Edit MOTD. Combined with the
            // rank-id range check `(rank - 8) <= 2 unsigned` (also in refresh), this means the
            // edit buttons should only ever be enabled for ranks 8/9/10. Setting low-rank perms
            // to 0 makes the panel hide the buttons for those members — preventing the crash
            // path entirely for new joiners at rank 1.
            const ulong HIGH_RANK_PERMS = 0xFFFFFFFFFFFFFFFFUL;   // all bits incl 0x10000 + 0x20000
            const ulong LOW_RANK_PERMS  = 0x0000000000000000UL;   // no privileges; buttons hidden
            w.WriteUInt32(10);
            for (uint rid = 1; rid <= 10; rid++)
            {
                w.WriteUInt32(rid);
                w.WriteCString("");                                // no rank name; panel shows the numeric id
                w.WriteUInt64(rid >= 8 ? HIGH_RANK_PERMS : LOW_RANK_PERMS);
            }

            // SECTION 3 — members
            w.WriteUInt32((uint)members.Count);
            foreach (var m in members)
            {
                // Per-member CharacterRepository fetch — we already need it for level/zone
                // properties below, so do the lookup once here and reuse. Also gives us the
                // stored posseRankId for accurate rank_id transmission.
                var memberSaved = CharacterRepository.GetCharacter(m.charId);
                uint memberRankId = m.isFounder ? 10u
                    : (memberSaved != null && memberSaved.posseRankId >= 1 && memberSaved.posseRankId <= 9
                        ? (uint)memberSaved.posseRankId : 1u);

                w.WriteByte(0x01);                            // present flag — non-zero → AddPosseMember body follows
                w.WriteUInt64(m.charId);                      // char_sql_id (zero-extended uint32 → uint64)
                w.WriteUInt32(memberRankId);                   // actual stored rank (founder forced to 10)
                w.WriteCString(m.name ?? "");                 // direct CString name (internal/lookup; displayed name comes from "name" property below)
                w.WriteUInt64(m.isFounder ? 0xFFFFFFFFFFFFFFFFUL : 0UL);

                // Per-member properties. The roster panel reads these via CachedPosseMember::Get*Property
                // accessors (Ghidra @ 0x00617d20–0x00617e44), each of which calls GetPropertyById(N) with
                // a small integer that switches to a PROPERTY_* String constant used as the map key.
                // PROPERTY_NAME at runtime = "5" (verified via x32dbg memory inspection of statics at
                // 0x00932F90..0x00932FA0 → pointer → +0x4 → ASCII), so the keys are stringified integer
                // IDs matching the GetPropertyById switch cases — same pattern as posse-level Section 4
                // properties (key "0" = Philosophy, "1" = MOTD).
                //   "0" — online (GetOnlineProperty): empty OR equals PROPERTY_BOOL_FALSE ("0")
                //         → offline; anything else (including "1") → online. PROPERTY_BOOL_FALSE
                //         content verified via x32dbg memory inspection — it's literally the
                //         ASCII digit "0" (not the word "false"), so we must send "0" / "1".
                //   "2" — world  (GetWorldProperty)
                //   "3" — zone   (GetZoneProperty)
                //   "4" — level  (GetLevelProperty, parsed via _strtol)
                //   "5" — name   (GetNameProperty, displayed character name)
                // Without these properties the roster falls back to "PlayerId: <id>(offline) Location NA
                // Rank <id> Lvl ---" via the format string at VA 0x00865C80 ("PlayerId: %I64d%s").
                var memberProps = new System.Collections.Generic.List<(string key, string val)>();
                memberProps.Add(("5", m.name ?? ""));    // name
                bool memberIsOnline = server == null ? true : onlineCharIds.Contains(m.charId);
                memberProps.Add(("0", memberIsOnline ? "1" : "0"));   // online status — "0" matches PROPERTY_BOOL_FALSE (verified literal), so client GetOnlineProperty returns false correctly.
                // memberSaved was fetched above for the rank lookup — reuse it here.
                if (memberSaved != null)
                {
                    memberProps.Add(("4", memberSaved.level.ToString()));               // level (decimal string, parsed via _strtol)
                    string zone = memberSaved.currentZoneName ?? "";                    // raw GCType — client maps to display name internally
                    memberProps.Add(("2", zone));                                        // world (in DR this is the zone-display field)
                    memberProps.Add(("3", zone));                                        // zone (display override; same value covers both panel reads)
                }

                w.WriteUInt32((uint)memberProps.Count);
                foreach (var p in memberProps)
                {
                    w.WriteCString(p.key);
                    w.WriteCString(p.val);
                }
            }

            // SECTION 4 — posse-level properties (optional, only if any are set)
            if (props.Count > 0)
            {
                w.WriteUInt32((uint)props.Count);
                foreach (var p in props)
                {
                    w.WriteCString(p.key);
                    w.WriteCString(p.val);
                }
            }

            byte[] packet = w.ToArray();
            Debug.LogError($"[POSSE] >> SENDING CachedPosseFull posseId={posse.Id} name='{posse.Name}' members={members.Count} props={props.Count} hex={BitConverter.ToString(packet)}");
            sendCompressed(conn, 0x01, 0x0F, packet);
        }
    }
}
