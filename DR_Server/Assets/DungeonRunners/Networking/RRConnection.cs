using System;
using System.Net.Sockets;
using UnityEngine;
using DungeonRunners.Data;
using DungeonRunners.Core;

namespace DungeonRunners.Networking
{
    public class RRConnection
    {
        public int ConnId { get; private set; }
        public TcpClient Client { get; private set; }
        public NetworkStream Stream { get; private set; }
        public object SendLock { get; } = new object();
        public bool IsConnected { get; set; }
        public string LoginName { get; set; }
        public uint PeerId24 { get; set; }

        public uint CurrentZoneId { get; set; }
        public string CurrentZoneGcType { get; set; } = "world.town";

        public float PlayerPosX { get; set; } = 480.0f;
        public float PlayerPosY { get; set; } = -191.0f;
        public float PlayerHeading { get; set; } = 0.0f;
        public byte SessionID { get; set; } = 0;
        public uint UnitBehaviorId { get; set; }
        public float LastPositionUpdateTime { get; set; } = 0f;
        public byte PendingLocalMoveSessionId { get; set; } = 0;
        public byte PendingLocalMoveCount { get; set; } = 0;
        public byte[] PendingLocalMoveData { get; set; } = Array.Empty<byte>();
        public float PendingLocalMoveFlushAt { get; set; } = 0f;
        public int LastCombatSyncFlushFrame { get; set; } = -1;
        public float IgnoreClientHPUntilTime { get; set; } = 0f;
        public bool PlayerHPClientTruthDirty { get; set; } = false;
        public float PlayerHPDirtySinceTime { get; set; } = 0f;
        public string PlayerHPDirtyReason { get; set; } = null;
        public uint LastAcceptedClientHPWire { get; set; } = 0;
        public float LastAcceptedClientHPTime { get; set; } = 0f;
        public int SuppressedPlayerHPSyncPackets { get; set; } = 0;
        public bool HasActiveUseTarget { get; set; } = false;
        public ushort ActiveUseTargetId { get; set; } = 0;
        public byte ActiveUseTargetFlags { get; set; } = 0;
        public ushort UnitContainerId { get; set; } = 0;
        public ushort ModifiersId { get; set; } = 0;
        public bool ZoneSpawnInvulnerabilityActive { get; set; } = false;
        public float ZoneSpawnInvulnerabilityExpiresAt { get; set; } = 0f;
        public float ZoneSpawnInvulnerabilitySentAt { get; set; } = 0f;
        public int ZoneSpawnInvulnerabilityClearFrame { get; set; } = -1;

        public bool IsAdmin { get; set; } = true;

        public ushort DialogManagerId { get; set; } = 0;
        public string CurrentDialogNpcId { get; set; } = null;
        public string PendingMerchantNpcGcClass { get; set; } = null;
        public ushort PendingMerchantComponentId { get; set; } = 0;
        public float PendingMerchantTargetX { get; set; } = 0f;
        public float PendingMerchantTargetY { get; set; } = 0f;
        public string ActiveMerchantNpcGcClass { get; set; } = null;
        public ushort ActiveMerchantComponentId { get; set; } = 0;
        public DateTime ActiveMerchantRefreshDueUtc { get; set; } = DateTime.MinValue;
        public bool ActiveMerchantRefreshReady { get; set; } = false;
        public bool HasActiveMerchantRefresh { get; set; } = false;
        public uint NextQuestInstanceId = 1;
        public uint PendingQuestHash { get; set; } = 0;
        public ushort QuestManagerId { get; set; } = 0;
        public uint PendingQuestNpcEntityId { get; set; } = 0;
        public uint ViewingQuestInstanceId { get; set; } = 0;
        public uint PendingTurnInInstanceId { get; set; } = 0;
        public bool IsAbandonConfirmed { get; set; } = false;
        public int AbandonClickCount { get; set; } = 0;

        public MessageQueue MessageQueue { get; private set; }
        public byte MovementGeneration = 0;
        public Coroutine TickCoroutine { get; set; }
        public GCObject Avatar { get; set; }
        public GCObject Player { get; set; }
        public uint UpdateNumber { get; set; } = 0;

        public float PendingSpawnX;
        public float PendingSpawnY;
        public float PendingSpawnZ;

        // ═══ MULTIPLAYER ═══
        public string CurrentZoneName { get; set; } = "";  // Exact zone name (e.g. "dungeon00_level01")
        public uint InstanceId { get; set; } = 0;          // Binary: ZoneClient::GotoInstance(int) — group-based instance
        public string AvatarGcType { get; set; } = "";
        public string ClassName { get; set; } = "Fighter";
        public ushort SkillsComponentId { get; set; } = 0;
        public ushort ManipulatorsComponentId { get; set; } = 0;
        public ushort ModifiersComponentId { get; set; } = 0;
        public ushort BehaviorComponentId { get; set; } = 0;
        public int PlayerLevel { get; set; } = 1;
        public uint CharSqlId { get; set; } = 0;  // Database character ID — set at character select
        public bool GroupConnectedSent { get; set; } = false;  // True once processConnected(0x30) sent — never resend
        public float PlayerPosZ { get; set; } = 0f;
        public bool HasLivePlayerPosition { get; set; } = false;
        public float LivePlayerPosX { get; set; } = 0f;
        public float LivePlayerPosY { get; set; } = 0f;
        public float LivePlayerPosZ { get; set; } = 0f;
        public float LivePlayerHeading { get; set; } = 0f;
        public float LivePlayerPositionTime { get; set; } = 0f;
        public bool IsSpawned { get; set; } = false;
        public bool NativeFullHPOnNextSpawn { get; set; } = false;
        public bool AllowFlush { get; set; } = false; // Per-connection flush gate — false during zone transitions

        // ═══ TOWN PORTAL ═══
        public bool HasSavedTownPortal { get; set; } = false;
        public string TownPortalZoneName { get; set; } = "";   // zone where portal was created
        public string TownPortalTargetZone { get; set; } = ""; // zone the portal sends you to
        public uint TownPortalZoneId { get; set; } = 0;
        public float TownPortalPosX { get; set; }
        public float TownPortalPosY { get; set; }
        public float TownPortalPosZ { get; set; }

        // ═══ ZONE PORTAL ═══
        public string ZonePortalSource { get; set; } = "";

        // ═══ DLL SECURITY ═══
        public uint DllSessionToken { get; set; } = 0;

        public RRConnection(int connId, TcpClient client, NetworkStream stream)
        {
            ConnId = connId;
            Client = client;
            Stream = stream;
            IsConnected = true;
            LoginName = null;
            PeerId24 = 0;
            CurrentZoneId = 0;
            MessageQueue = new MessageQueue();
        }

        public void Disconnect()
        {
            IsConnected = false;
            try
            {
                Stream?.Close();
                Client?.Close();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[RRConnection] Error during disconnect: {ex.Message}");
            }
        }
    }
}
