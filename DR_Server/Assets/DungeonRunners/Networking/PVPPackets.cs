using System;
using UnityEngine;
using DungeonRunners.Utilities;

namespace DungeonRunners.Networking
{
    /// <summary>
    /// Builds PVP protocol packets on GroupClient channel 0x09.
    /// Binary+PDB verified from DungeonRunners.exe.
    ///
    /// Packet format: [0x09] [type] [payload...]
    /// Sent via SendToClient → SendCompressedA(conn, 0x01, 0x0F, data)
    ///
    /// DISPATCH TABLE (PVP-relevant entries from GroupClient::processMessage):
    ///   0x4E -> processChangedPVPStatus      (0x5F9920)  171 bytes
    ///   0x4F -> ReadPVPDuelStatus             (0x5F99D0)  606 bytes
    ///
    /// CLIENT→SERVER OPCODES (GroupClient channel 0x0B):
    ///   0x29 = enterPVPZone      (no payload)
    ///   0x2A = requestPVPMatch   (match archetype data)
    ///   0x2B = cancelPVPMatch    (no payload)
    ///   0x2C = leavePVP          (no payload)
    ///   0x2D = requestPVPDuel    (4 bytes: CharSQLID)
    ///   0x2E = acceptPVPDuel     (no payload)
    ///   0x2F = declinePVPDuel    (no payload)
    /// </summary>
    public static class PVPPackets
    {
        // ── Duel Status Responses ──────────────────────────────────
        // TYPE 0x4E -- processChangedPVPStatus
        // Binary @ 0x5F9920 (171 bytes)
        // Sends PVP state change notification to client.
        // ReadPVPDuelStatus is at 0x4F and reads the duel-specific state.

        /// <summary>
        /// Duel status enum matching client expectations.
        /// </summary>
        public enum DuelStatusType : byte
        {
            None = 0,
            Challenged = 1,      // "X challenges you to a duel"
            Accepted = 2,        // Both players accept, countdown starts
            InProgress = 3,      // Combat active
            Won = 4,             // You won
            Lost = 5,            // You lost
            Declined = 6,        // Target declined
            Cancelled = 7,       // Challenger cancelled
        }

        /// <summary>
        /// Build a PVP duel status packet.
        /// TYPE 0x4F — ReadPVPDuelStatus @ 0x5F99D0.
        /// Binary reads via vtable dispatch (DFCInputStream):
        ///   read uint32 → duel state
        ///   read uint32 → opponent CharSQLID
        ///   read uint32 → opponent entity handle (for targeting)
        ///   read uint32 → result/rating change
        /// </summary>
        public static byte[] BuildDuelStatus(DuelStatusType status,
            uint opponentCharSqlId, uint opponentEntityHandle, int ratingChange)
        {
            var w = new LEWriter();
            w.WriteByte(0x09);           // Channel = GroupClient
            w.WriteByte(0x4F);           // Type = ReadPVPDuelStatus
            w.WriteUInt32((uint)status);
            w.WriteUInt32(opponentCharSqlId);
            w.WriteUInt32(opponentEntityHandle);
            w.WriteInt32(ratingChange);
            return w.ToArray();
        }

        /// <summary>
        /// Build a PVP status change notification.
        /// TYPE 0x4E — processChangedPVPStatus @ 0x5F9920 (171 bytes).
        /// Notifies the client that PVP state has changed (flagged, in match, etc.)
        /// </summary>
        public static byte[] BuildPVPStatusChanged(byte pvpState, uint matchId)
        {
            var w = new LEWriter();
            w.WriteByte(0x09);           // Channel = GroupClient
            w.WriteByte(0x4E);           // Type = processChangedPVPStatus
            w.WriteUInt32((uint)pvpState);
            w.WriteUInt32(matchId);
            return w.ToArray();
        }

        // ── PVP Queue Responses ────────────────────────────────────
        // These are for Phase 3 (match system), stubbed for now.

        /// <summary>
        /// TYPE for processEnterPVPZoneBailed @ 0x5F9680 — player's zone entry failed.
        /// TYPE for processEnterPVPQueueYellow @ 0x5F9700 — player got deserter debuff.
        /// TYPE for processEnterPVPQueueDisconnected @ 0x5F9780 — disconnected from queue.
        /// These types need further RE; stubbed until Phase 3.
        /// </summary>
    }
}
