using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DungeonRunners.Managers
{
    /// <summary>
    /// Server-side group system — mirrors binary GroupClient architecture.
    /// Binary: GroupClient has inviteUser, acceptInvite, declineInvite, setLeader,
    ///         removeUser, setMonsterDifficulty, resetInstances, gotoMember.
    /// Binary: ServerEntityManager::writeClientUpdateMessages sends PER-CLIENT updates.
    /// Binary: DungeonGenerator::generate(Random) — same seed = same dungeon.
    /// </summary>
    public class GroupManager
    {
        private static GroupManager _instance;
        public static GroupManager Instance => _instance ??= new GroupManager();

        private Dictionary<uint, Group> _groups = new Dictionary<uint, Group>();
        private Dictionary<int, uint> _connToGroup = new Dictionary<int, uint>();  // connId -> groupId
        private Dictionary<int, uint> _pendingInvites = new Dictionary<int, uint>();  // invitee connId -> groupId
        private Dictionary<string, uint> _loginToGroup = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);  // loginName -> groupId (survives disconnect)
        private uint _nextGroupId = 1;

        // ═══════════════════════════════════════════════════════════
        // GROUP LIFECYCLE — per binary GroupClient::connect/start/stop
        // ═══════════════════════════════════════════════════════════

        /// <summary>Create a new group with this player as leader.</summary>
        public Group CreateGroup(int leaderConnId, string leaderName, string loginName = null)
        {
            // Remove from any existing group first
            LeaveGroup(leaderConnId);

            var group = new Group
            {
                GroupId = _nextGroupId++,
                LeaderConnId = leaderConnId,
                MonsterDifficulty = 0,  // RECRUIT default
                InstanceSeed = GenerateInstanceSeed(),
            };
            group.Members.Add(new GroupMember
            {
                ConnId = leaderConnId,
                Name = leaderName,
                LoginName = loginName ?? "",
                IsOnline = true,
                CurrentZoneName = ""
            });

            _groups[group.GroupId] = group;
            _connToGroup[leaderConnId] = group.GroupId;
            if (!string.IsNullOrEmpty(loginName))
                _loginToGroup[loginName] = group.GroupId;

            Debug.LogError($"[GROUP] Created group {group.GroupId}, leader={leaderName} (conn={leaderConnId}), seed=0x{group.InstanceSeed:X8}");
            return group;
        }

        // ═══════════════════════════════════════════════════════════
        // INVITE SYSTEM — per binary GroupClient::inviteUser/acceptInvite/declineInvite
        // ═══════════════════════════════════════════════════════════

        /// <summary>Invite a player to the group. Binary: GroupClient::inviteUser(CharSQLID)</summary>
        public bool InvitePlayer(int inviterConnId, int inviteeConnId)
        {
            var group = GetGroupForConn(inviterConnId);
            if (group == null)
            {
                Debug.LogError($"[GROUP] InvitePlayer: inviter {inviterConnId} not in a group");
                return false;
            }
            if (group.LeaderConnId != inviterConnId)
            {
                Debug.LogError($"[GROUP] InvitePlayer: {inviterConnId} is not leader");
                return false;
            }
            if (IsInGroup(inviteeConnId))
            {
                Debug.LogError($"[GROUP] InvitePlayer: {inviteeConnId} already in a group");
                return false;
            }
            if (group.Members.Count >= 5)
            {
                Debug.LogError($"[GROUP] InvitePlayer: group {group.GroupId} is full (5 max)");
                return false;
            }

            _pendingInvites[inviteeConnId] = group.GroupId;
            Debug.LogError($"[GROUP] Invite sent: group {group.GroupId} -> conn {inviteeConnId}");
            return true;
        }

        /// <summary>Accept a pending invite. Binary: GroupClient::acceptInvite(K)</summary>
        public Group AcceptInvite(int inviteeConnId, string inviteeName, string loginName = null)
        {
            if (!_pendingInvites.TryGetValue(inviteeConnId, out uint groupId))
            {
                Debug.LogError($"[GROUP] AcceptInvite: no pending invite for {inviteeConnId}");
                return null;
            }
            _pendingInvites.Remove(inviteeConnId);

            if (!_groups.TryGetValue(groupId, out var group))
            {
                Debug.LogError($"[GROUP] AcceptInvite: group {groupId} no longer exists");
                return null;
            }

            // Leave any existing group
            LeaveGroup(inviteeConnId);

            group.Members.Add(new GroupMember
            {
                ConnId = inviteeConnId,
                Name = inviteeName,
                LoginName = loginName ?? "",
                IsOnline = true,
                CurrentZoneName = ""
            });
            _connToGroup[inviteeConnId] = groupId;
            if (!string.IsNullOrEmpty(loginName))
                _loginToGroup[loginName] = groupId;

            Debug.LogError($"[GROUP] {inviteeName} joined group {groupId} (now {group.Members.Count} members)");
            return group;
        }

        /// <summary>Decline a pending invite. Binary: GroupClient::declineInvite(K)</summary>
        public void DeclineInvite(int inviteeConnId)
        {
            _pendingInvites.Remove(inviteeConnId);
            Debug.LogError($"[GROUP] Invite declined by conn {inviteeConnId}");
        }

        // ═══════════════════════════════════════════════════════════
        // GROUP MANAGEMENT — per binary GroupClient methods
        // ═══════════════════════════════════════════════════════════

        /// <summary>Remove player from group permanently. Binary: GroupClient::removeUser(CharSQLID)</summary>
        public void LeaveGroup(int connId)
        {
            if (!_connToGroup.TryGetValue(connId, out uint groupId)) return;
            if (!_groups.TryGetValue(groupId, out var group)) return;

            var member = group.Members.Find(m => m.ConnId == connId);
            if (member != null && !string.IsNullOrEmpty(member.LoginName))
                _loginToGroup.Remove(member.LoginName);

            group.Members.RemoveAll(m => m.ConnId == connId);
            _connToGroup.Remove(connId);

            Debug.LogError($"[GROUP] Conn {connId} left group {groupId} ({group.Members.Count} remaining)");

            if (group.Members.Count == 0)
            {
                // Disband empty group
                _groups.Remove(groupId);
                Debug.LogError($"[GROUP] Group {groupId} disbanded (empty)");
            }
            else if (group.LeaderConnId == connId)
            {
                // Transfer leadership to first ONLINE member, or first member if none online
                var newLeader = group.Members.Find(m => m.IsOnline) ?? group.Members[0];
                group.LeaderConnId = newLeader.ConnId;
                Debug.LogError($"[GROUP] Leadership transferred to {newLeader.Name} (conn={group.LeaderConnId})");
            }
        }

        /// <summary>
        /// Mark member as disconnected but keep in group.
        /// Binary: processMemberDisconnected@0x5F9920 fires event 0x121112.
        /// Member stays in group for reconnect via processMemberReconnected@0x5F99D0.
        /// </summary>
        public bool DisconnectMember(int connId)
        {
            if (!_connToGroup.TryGetValue(connId, out uint groupId)) return false;
            if (!_groups.TryGetValue(groupId, out var group)) return false;

            var member = group.Members.Find(m => m.ConnId == connId);
            if (member == null) return false;

            member.IsOnline = false;
            _connToGroup.Remove(connId);
            // Keep _loginToGroup so ReconnectMember can find the group
            // Do NOT transfer leadership — disconnect is temporary.
            // Leader stays as leader even while offline. On reconnect they resume as leader.

            Debug.LogError($"[GROUP] Member {member.Name} disconnected from group {groupId} (kept in group)");
            return true;
        }

        /// <summary>
        /// Reconnect a previously disconnected member.
        /// Binary: processMemberReconnected@0x5F99D0 restores member state.
        /// Returns the group if reconnect succeeded, null otherwise.
        /// </summary>
        public Group ReconnectMember(string loginName, int newConnId)
        {
            if (string.IsNullOrEmpty(loginName)) return null;
            if (!_loginToGroup.TryGetValue(loginName, out uint groupId)) return null;
            if (!_groups.TryGetValue(groupId, out var group)) return null;

            var member = group.Members.Find(m =>
                m.LoginName.Equals(loginName, StringComparison.OrdinalIgnoreCase));
            if (member == null) return null;

            member.ConnId = newConnId;
            member.IsOnline = true;
            _connToGroup[newConnId] = groupId;

            Debug.LogError($"[GROUP] Member {member.Name} reconnected to group {groupId} (conn={newConnId})");
            return group;
        }

        /// <summary>Set group leader. Binary: GroupClient::setLeader(CharSQLID)</summary>
        public bool SetLeader(int requestorConnId, int newLeaderConnId)
        {
            var group = GetGroupForConn(requestorConnId);
            if (group == null || group.LeaderConnId != requestorConnId) return false;
            if (!group.Members.Any(m => m.ConnId == newLeaderConnId)) return false;

            group.LeaderConnId = newLeaderConnId;
            Debug.LogError($"[GROUP] Leader changed to conn {newLeaderConnId} in group {group.GroupId}");
            return true;
        }

        /// <summary>Set monster difficulty. Binary: GroupClient::setMonsterDifficulty(byte)</summary>
        public void SetMonsterDifficulty(int connId, byte difficulty)
        {
            var group = GetGroupForConn(connId);
            if (group == null) return;
            group.MonsterDifficulty = difficulty;
            Debug.LogError($"[GROUP] Monster difficulty set to {difficulty} for group {group.GroupId}");
        }

        /// <summary>Reset dungeon instances. Binary: GroupClient::resetInstances()</summary>
        public void ResetInstances(int connId)
        {
            var group = GetGroupForConn(connId);
            if (group == null) return;
            if (group.LeaderConnId != connId)
            {
                Debug.LogError($"[GROUP] ResetInstances: conn {connId} is not leader");
                return;
            }

            // New seed = new dungeon layout
            group.InstanceSeed = GenerateInstanceSeed();

            // Clear spawned zones for this group
            foreach (var zoneName in group.SpawnedZones.ToList())
            {
                ZoneSpawnManager.Instance.ResetZone($"{zoneName}_{group.GroupId}");
            }
            group.SpawnedZones.Clear();

            Debug.LogError($"[GROUP] Instances reset for group {group.GroupId}, new seed=0x{group.InstanceSeed:X8}");
        }

        // ═══════════════════════════════════════════════════════════
        // ZONE TRACKING — per binary GroupClient::processUserChangedZone
        // ═══════════════════════════════════════════════════════════

        /// <summary>Update member's current zone. Binary: processUserChangedZone</summary>
        public void UpdateMemberZone(int connId, string zoneName)
        {
            var group = GetGroupForConn(connId);
            if (group == null) return;

            var member = group.Members.Find(m => m.ConnId == connId);
            if (member != null)
            {
                member.CurrentZoneName = zoneName;
                Debug.LogError($"[GROUP] Member conn {connId} zone updated to '{zoneName}' in group {group.GroupId}");
            }
        }

        /// <summary>Mark member online/offline. Binary: processMemberDisconnected/Reconnected</summary>
        public void SetMemberOnline(int connId, bool online)
        {
            var group = GetGroupForConn(connId);
            if (group == null) return;

            var member = group.Members.Find(m => m.ConnId == connId);
            if (member != null)
                member.IsOnline = online;
        }

        // ═══════════════════════════════════════════════════════════
        // QUERIES
        // ═══════════════════════════════════════════════════════════

        public Group GetGroupForConn(int connId)
        {
            if (!_connToGroup.TryGetValue(connId, out uint groupId)) return null;
            _groups.TryGetValue(groupId, out var group);
            return group;
        }

        public bool IsInGroup(int connId) => _connToGroup.ContainsKey(connId);

        public bool HasPendingInvite(int connId) => _pendingInvites.ContainsKey(connId);

        /// <summary>Get the group that has a pending invite for this connId (for decline notifications).</summary>
        public Group GetPendingInviteGroup(int connId)
        {
            if (!_pendingInvites.TryGetValue(connId, out uint groupId)) return null;
            _groups.TryGetValue(groupId, out var group);
            return group;
        }

        /// <summary>Get all group members in the same zone as the given connection.</summary>
        public List<GroupMember> GetMembersInZone(int connId, string zoneName)
        {
            var group = GetGroupForConn(connId);
            if (group == null) return new List<GroupMember>();
            return group.Members.Where(m => m.IsOnline &&
                string.Equals(m.CurrentZoneName, zoneName, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>Get all OTHER group members in same zone (excludes self).</summary>
        public List<GroupMember> GetOtherMembersInZone(int connId, string zoneName)
        {
            return GetMembersInZone(connId, zoneName).Where(m => m.ConnId != connId).ToList();
        }

        /// <summary>Get the group's shared RNG seed for dungeon generation.</summary>
        public uint GetGroupSeed(int connId)
        {
            var group = GetGroupForConn(connId);
            return group?.InstanceSeed ?? (uint)(DateTime.Now.Ticks & 0xFFFFFFFF);
        }

        /// <summary>Check if zone mobs are already spawned for this group instance.</summary>
        public bool IsZoneSpawnedForGroup(int connId, string zoneName)
        {
            var group = GetGroupForConn(connId);
            if (group == null) return false;
            return group.SpawnedZones.Contains(zoneName);
        }

        /// <summary>Mark zone as spawned for the group.</summary>
        public void MarkZoneSpawned(int connId, string zoneName)
        {
            var group = GetGroupForConn(connId);
            if (group == null) return;
            group.SpawnedZones.Add(zoneName);
        }

        private uint GenerateInstanceSeed()
        {
            return (uint)(DateTime.Now.Ticks & 0xFFFFFFFF) ^ (uint)UnityEngine.Random.Range(0, int.MaxValue);
        }
    }

    // ═══════════════════════════════════════════════════════════
    // DATA CLASSES — per binary Group/GroupMember/Member@GroupClient
    // ═══════════════════════════════════════════════════════════

    public class Group
    {
        public uint GroupId;
        public int LeaderConnId;
        public List<GroupMember> Members = new List<GroupMember>();
        public byte MonsterDifficulty;  // Binary: GroupClient::setMonsterDifficulty
        public uint InstanceSeed;       // Binary: DungeonGenerator seed — shared by all members
        public HashSet<string> SpawnedZones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool IsOpen;             // Binary: GC+0xD8 bit 0 — set by setOpenGroup (type 0x24)
        public byte InviteMode = 3;     // Binary: GC+0xB5 — set by setInviteMode (type 0x28). 3=anyone default
    }

    public class GroupMember
    {
        public int ConnId;
        public string Name;
        public string LoginName;
        public bool IsOnline;
        public string CurrentZoneName;
        public uint CharSqlId;       // cached for offline member packets
        public uint AvatarEntityId;  // cached for offline member packets
    }
}