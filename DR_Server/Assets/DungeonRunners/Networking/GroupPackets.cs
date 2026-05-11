using System;
using UnityEngine;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    /// <summary>
    /// Builds GroupClient protocol packets on channel 0x09.
    /// Binary+PDB verified: GroupClient::processMessage at VA 0x5F7E20.
    /// Two-level dispatch: (typeByte - 0x30) -> byte table at 0x5F80B0 -> jump table at 0x5F8048.
    ///
    /// Packet format: [0x09] [type] [payload...]
    /// Wrapped in SendCompressedA(conn, 0x01, 0x0F, data)
    ///
    /// BINARY+PDB-VERIFIED WIRE TYPE MAP (S65 -- dispatch table extraction):
    ///   0x30 -> processConnected              (0x5F80E0)  PDB confirmed
    ///   0x32 -> processInvitation             (0x5F8210)  PDB confirmed
    ///   0x33 -> processUnvitation             (0x5F83C0)  PDB confirmed
    ///   0x34 -> processInviteResult           (0x5F8490)  PDB confirmed
    ///   0x35 -> processUserChangedGroup       (0x5F8960)  PDB confirmed
    ///   0x42 -> processAddUser                (0x5F8D30)  PDB confirmed
    ///   0x43 -> processRemoveUser             (0x5F8F50)  PDB confirmed  [was 0x4B WRONG]
    ///   0x44 -> processSetLeader              (0x5F9080)  PDB confirmed
    ///   0x45 -> processChangedInviteMode      (0x5F9260)  PDB confirmed
    ///   0x4B -> processMemberHealthManaChange (0x5FA510)  PDB confirmed  [was 0x50 WRONG]
    ///   0x4C -> processUserChangedZone        (0x5FA100)  PDB confirmed
    ///   0x4E -> processMemberDisconnected     (0x5F9920)  PDB confirmed
    ///   0x4F -> processMemberReconnected      (0x5F99D0)  PDB confirmed
    ///   0x55 -> processMonsterDifficultySet   (0x5FA320)  PDB confirmed  [was 0x51 WRONG]
    /// </summary>
    public static class GroupPackets
    {
        // TYPE 0x30 -- processConnected
        // Binary: 0x5F80E0
        // Reads: uint32 leaderCharId, byte difficulty, byte inviteMode
        // Then HARDCODES: GC+0xD0=0, GC+0xD4=[0x932E58]=0, clears members
        public static byte[] BuildProcessConnected(uint leaderCharId,
            byte difficulty, byte inviteMode)
        {
            var w = new LEWriter();
            w.WriteByte(0x09);          // Channel = GroupClient (TTD-verified)
            w.WriteByte(0x30);          // Type = processConnected
            w.WriteUInt32(leaderCharId);
            w.WriteByte(difficulty);
            w.WriteByte(inviteMode);
            return w.ToArray();
        }

        // TYPE 0x35 -- processUserChangedGroup
        // Binary: 0x5F8960. Sets GC+0xD0 (groupId) and GC+0xD4 (selfCharSqlId).
        //
        // S65C BINARY-VERIFIED FIELD MAP:
        //   [uint32 groupId]       → GC+0xD0 (0x5F89B8)
        //   [uint32 selfCharSqlId] → GC+0xD4 (0x5F89DF) — MUST be receiving player's OWN charSqlId!
        //     kickMember@0x5F70A0 checks: cmp [GC+0xD4], [GC+0xB0] (leaderCharSqlId)
        //     promoteMember@0x5F7170 checks same gate
        //     If GC+0xD4 != GC+0xB0 → client silently drops kick/promote
        //   [byte flag]            → GC+0xB7 (0x5F8A08)
        //   [byte isOpenGroup]     → GC+0xD8 bit 0 (0x5F8A28-0x5F8A3E) — NOT isLeader!
        //     isLeader (GC+0xB4) is computed CLIENT-SIDE by sub@0x5F6450
        //     from comparing member charIds against GC+0xB0 (leaderCharId from 0x30)
        //
        // S65 RE of sub @ 0x5F9390 (called at 0x5F8A48):
        //   0x5F93B0: push 4, call edx → read uint32 selfId1
        //   0x5F93CC: push 4, call edx → read uint32 selfId2
        //   0x5F93E3: call readType    → tagged GC type (PVPMatch check)
        //   0x5F9403: call readType    → tagged GC type (PVPTeam check)
        //
        // readType @ 0x5E3C40 reads a TAG BYTE: 0x00=null(1 byte), 0x01=u8, 0x02=u16, 0x04=u32, 0xFF=string
        // For non-PVP: tag 0x00 = null → IsKindOf returns false → both set to null. Clean exit.
        //
        // Then back in processUserChangedGroup @ 0x5F8AD9: read byte memberCount
        //
        // Full wire format after [ch][type]:
        //   [uint32 groupId][uint32 selfCharSqlId][byte flag][byte isOpenGroup]
        //   [uint32 selfId1][uint32 selfId2]  ← raw uint32s for entity lookup
        //   [byte 0x00][byte 0x00]            ← readType null tags (PVPMatch, PVPTeam)
        //   [byte memberCount][members...]
        public static byte[] BuildProcessUserChangedGroup(uint groupId, uint selfCharSqlId,
            byte flag, byte isOpenGroup, uint selfEntityId1, uint selfEntityId2,
            byte memberCount, GroupMemberInfo[] members = null)
        {
            var w = new LEWriter();
            w.WriteByte(0x09);
            w.WriteByte(0x35);
            w.WriteUInt32(groupId);         // -> GC+0xD0
            w.WriteUInt32(selfCharSqlId);   // -> GC+0xD4 (kick/promote gate: must == GC+0xB0 for leader)
            w.WriteByte(flag);              // -> GC+0xB7
            w.WriteByte(isOpenGroup);       // -> GC+0xD8 bit 0 (isOpenGroup flag)
            // Sub @ 0x5F9390 reads:
            w.WriteUInt32(selfEntityId1);   // raw uint32 #1
            w.WriteUInt32(selfEntityId2);   // raw uint32 #2
            w.WriteByte(0x00);              // readType #1: tag 0x00 = null PVPMatch
            w.WriteByte(0x00);              // readType #2: tag 0x00 = null PVPTeam
            // Back in processUserChangedGroup:
            w.WriteByte(memberCount);
            if (members != null)
            {
                foreach (var m in members)
                    WriteMember(w, m);
            }
            return w.ToArray();
        }

        // TYPE 0x32 -- processInvitation
        // Binary: 0x5F8210. Shows GroupInviteDialog.
        // Reads: uint32 inviteId, uint32 groupId, string name, byte flags
        public static byte[] BuildProcessInvitation(uint inviteId, uint groupId, string inviterName, byte flags)
        {
            var w = new LEWriter();
            w.WriteByte(0x09);
            w.WriteByte(0x32);
            w.WriteUInt32(inviteId);    // -> GC+0xF0 (client returns in accept/decline)
            w.WriteUInt32(groupId);     // -> GC+0xF4
            w.WriteCString(inviterName);// -> GC+0xF8
            w.WriteByte(flags);         // -> GC+0xFC
            return w.ToArray();
        }

        // TYPE 0x33 -- processUnvitation
        // Binary: 0x5F83C0. Cancels a pending invitation.
        public static byte[] BuildProcessUnvitation(uint inviteId)
        {
            var w = new LEWriter();
            w.WriteByte(0x09);
            w.WriteByte(0x33);
            w.WriteUInt32(inviteId);
            return w.ToArray();
        }

        // TYPE 0x34 -- processInviteResult
        // Binary: 0x5F8490. Sub-dispatch with 23 sub-types via table at 0x5F8904.
        // Reads: byte subType, string name
        //
        // BINARY-VERIFIED subtypes (S65C):
        // Sub 0x00 = "Invite sent to %s."
        // Sub 0x01 = "%s has accepted your invitation."
        // Sub 0x02 = "%s has declined your invitation."
        // Sub 0x03 = "%s has logged off.  Invitation declined."
        // Sub 0x04 = "Unknown invite error." (also 0x05, 0x10)
        // Sub 0x06 = "%s, you cannot invite yourself to your own group."
        // Sub 0x07 = "Can't invite %s.  You are not the group leader."
        // Sub 0x08 = "Can't invite %s while you are invited to another group."
        // Sub 0x09 = "%s was not found on this server."
        // Sub 0x0A = "%s is not currently accepting invites." (also 0x12, 0x13)
        // Sub 0x0B = "%s is already in your group."
        // Sub 0x0C = "%s is currently in another group."
        // Sub 0x0D = "%s is already invited to your group."
        // Sub 0x0E = "%s is currently invited to another group."
        // Sub 0x0F = "You are already invited to %s's group."
        // Sub 0x11 = "Failed to invite %s. Your group is full."
        // Sub 0x14 = "You can't invite other players during a PvP session."
        // Sub 0x15 = "%s is in a PvP session and cannot be invited right now."
        // Sub 0x16 = "Your invite to %s has timed out.  Try again later."
        public static byte[] BuildInviteResult(byte subType, string playerName)
        {
            var w = new LEWriter();
            w.WriteByte(0x09);
            w.WriteByte(0x34);
            w.WriteByte(subType);
            w.WriteCString(playerName);
            return w.ToArray();
        }

        // TYPE 0x42 -- processAddUser
        // Binary: 0x5F8D30. Reads: uint32 groupId, then readMember.
        public static byte[] BuildProcessAddUser(uint groupId, GroupMemberInfo member)
        {
            var w = new LEWriter();
            w.WriteByte(0x09);
            w.WriteByte(0x42);
            w.WriteUInt32(groupId);
            WriteMember(w, member);
            return w.ToArray();
        }

        // TYPE 0x43 -- processRemoveUser
        // Binary: 0x5F8F50. PDB-verified.
        // Reads: uint32 groupId (compare GC+0xD0), uint32 charSQLID (find in member vector)
        // *** S65 FIX: was 0x4B (WRONG -- that is processMemberHealthManaChange) ***
        public static byte[] BuildProcessRemoveUser(uint groupId, uint charId)
        {
            var w = new LEWriter();
            w.WriteByte(0x09);
            w.WriteByte(0x43);          // PDB-verified [was 0x4B WRONG]
            w.WriteUInt32(groupId);
            w.WriteUInt32(charId);
            return w.ToArray();
        }

        // TYPE 0x44 -- processSetLeader
        // Binary: 0x5F9080. "You are now the group leader." / "%s is now the group leader."
        //
        // BINARY FORMAT (verified by disasm):
        //   [uint32 groupId]          — verified against GC+0xD0
        //   [uint32 newLeaderCharId]  — written to GC+0xD4, used to find member in vector
        //   [byte flag]               — written to GC+0xB7 (0xFF = normal)
        //
        // NOTE: GC+0xD4 gets overwritten with charSqlId — must follow up with 0x35
        //       to restore correct zoneId for invite gate (0x46F4FA check).
        public static byte[] BuildProcessSetLeader(uint groupId, uint newLeaderCharId)
        {
            var w = new LEWriter();
            w.WriteByte(0x09);
            w.WriteByte(0x44);
            w.WriteUInt32(groupId);
            w.WriteUInt32(newLeaderCharId);
            w.WriteByte(0xFF);  // flag → GC+0xB7 (matches processConnected default)
            return w.ToArray();
        }

        // TYPE 0x45 -- processChangedInviteMode
        // Binary: 0x5F9260. PDB-verified.
        public static byte[] BuildChangedInviteMode(byte mode)
        {
            var w = new LEWriter();
            w.WriteByte(0x09);
            w.WriteByte(0x45);          // PDB-verified @ 0x5F9260
            w.WriteByte(mode);
            return w.ToArray();
        }

        // TYPE 0x4B -- processMemberHealthManaChange
        // Binary: 0x5FA510. PDB-verified. Updates GroupHealth HP/MP bars.
        //
        // BINARY FORMAT (verified by disasm):
        //   [uint32 charSQLID]
        //   [byte packedHpMp]  — high nibble (>>4) = HP fraction 0-15
        //                        low nibble (&0xF) = MP fraction 0-15
        //                        scale = 0.0667 (1/15), so 15 = 100%
        //                        0xFF = full HP + full MP
        //
        // *** S65 FIX: was 0x50 (WRONG type) ***
        // *** S65C FIX: was sending 4×uint32 (WRONG format — binary reads 1 packed byte) ***
        public static byte[] BuildMemberHealthMana(uint charId, byte hpFraction15, byte mpFraction15)
        {
            var w = new LEWriter();
            w.WriteByte(0x09);
            w.WriteByte(0x4B);          // PDB-verified @ 0x5FA510
            w.WriteUInt32(charId);
            byte packed = (byte)(((hpFraction15 & 0xF) << 4) | (mpFraction15 & 0xF));
            w.WriteByte(packed);
            return w.ToArray();
        }

        // TYPE 0x4C -- processUserChangedZone
        // Binary: 0x5FA100. PDB-verified. Notifies group of member zone change.
        public static byte[] BuildUserChangedZone(uint groupId, uint charSQLID, string zoneName)
        {
            var w = new LEWriter();
            w.WriteByte(0x09);
            w.WriteByte(0x4C);          // PDB-verified @ 0x5FA100
            w.WriteUInt32(groupId);
            w.WriteUInt32(charSQLID);
            w.WriteCString(zoneName);
            return w.ToArray();
        }

        // TYPE 0x49 -- processMemberDisconnected (NOT 0x4E — that's a different handler)
        // Binary: 0x5F9E80. "Group member '%s' has disconnected."
        // Dispatch: byte table index 25 → jump table index 11 → 0x5F7F53 → call 0x5F9E80
        // Reads: uint32 groupId (must match GC+0xD0), uint32 charId (member+0xC lookup)
        // Sets: member+0x18=0 (offline), member+0x30=1 (triggers HP bar disconnect state)
        public static byte[] BuildMemberDisconnected(uint groupId, uint charId)
        {
            var w = new LEWriter();
            w.WriteByte(0x09);
            w.WriteByte(0x49);
            w.WriteUInt32(groupId);
            w.WriteUInt32(charId);
            return w.ToArray();
        }

        // TYPE 0x4A -- processMemberReconnected (NOT 0x4F — that's a different handler)
        // Binary: 0x5F9FF0.
        // Dispatch: byte table index 25 → jump table index 12 → 0x5F7F63 → call 0x5F9FF0
        // Reads: uint32 groupId (must match GC+0xD0), uint32 charId (member+0xC lookup)
        // Sets: member+0x18=1 (online), member+0x30=1, fires event 0x121110
        public static byte[] BuildMemberReconnected(uint groupId, uint charId)
        {
            var w = new LEWriter();
            w.WriteByte(0x09);
            w.WriteByte(0x4A);
            w.WriteUInt32(groupId);
            w.WriteUInt32(charId);
            return w.ToArray();
        }

        // TYPE 0x55 -- processMonsterDifficultySet
        // Binary: 0x5FA320. PDB-verified.
        // *** S65 FIX: was 0x51 (WRONG) ***
        public static byte[] BuildMonsterDifficulty(uint leaderCharId, byte difficulty)
        {
            var w = new LEWriter();
            w.WriteByte(0x09);
            w.WriteByte(0x55);          // PDB-verified @ 0x5FA320 [was 0x51 WRONG]
            w.WriteUInt32(leaderCharId);
            w.WriteByte(difficulty);
            return w.ToArray();
        }

        // readMember -- writes member data in stream format
        // Binary: readMember@GroupClient @ 0x5FA6D0
        // Reads: charSQLID, name, avatarEntityId, isOnline
        private static void WriteMember(LEWriter w, GroupMemberInfo m)
        {
            w.WriteUInt32(m.CharSQLID);
            w.WriteCString(m.Name);
            w.WriteUInt32(m.AvatarEntityId);
            w.WriteByte((byte)(m.IsOnline ? 1 : 0));
        }
    }

    /// <summary>
    /// Member info for group packets. Maps to Member@GroupClient in binary.
    /// </summary>
    public class GroupMemberInfo
    {
        public uint CharSQLID;
        public string Name;
        public uint AvatarEntityId;
        public bool IsOnline;
    }
}