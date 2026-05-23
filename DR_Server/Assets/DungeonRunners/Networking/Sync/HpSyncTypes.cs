using DungeonRunners.Combat;
using DungeonRunners.Networking;

namespace DungeonRunners.Networking.Sync
{
    public enum HpOwnerKind
    {
        Unknown = 0,
        PlayerAvatar,
        Monster,
        NpcUnit,
        NonUnit
    }

    public enum HpReportSource
    {
        Unknown = 0,
        ClientEntitySynchSuffix,
        ClientSendUpdate,
        ClientDllHpHook,
        ClientPlayerStateSync,
        ServerMonsterAttack,
        ServerPlayerAttack,
        ServerSkillDamage,
        ServerPotionHeal,
        ServerSkillHeal,
        ServerNativeRegen,
        ServerEquipmentChange,
        ServerStatAllocation,
        ServerLevelUp,
        ServerRespawn,
        ServerZoneLoad,
        PersistenceLoad
    }

    public enum HpMaxChangeMode
    {
        ClampCurrent,
        PreserveFlat,
        PreservePercent,
        FillToMax,
        AddDeltaToCurrent
    }

    public readonly struct HpOwnerRef
    {
        public readonly HpOwnerKind Kind;
        public readonly uint EntityId;
        public readonly RRConnection Connection;
        public readonly Monster Monster;
        public readonly string DebugName;

        public HpOwnerRef(HpOwnerKind kind, uint entityId, RRConnection connection, Monster monster, string debugName)
        {
            Kind = kind;
            EntityId = entityId;
            Connection = connection;
            Monster = monster;
            DebugName = debugName;
        }

        public static HpOwnerRef Player(uint entityId, RRConnection conn)
        {
            return new HpOwnerRef(HpOwnerKind.PlayerAvatar, entityId, conn, null, conn?.LoginName ?? conn?.ConnId.ToString() ?? entityId.ToString());
        }

        public static HpOwnerRef MonsterOwner(Monster monster)
        {
            return new HpOwnerRef(HpOwnerKind.Monster, monster != null ? monster.EntityId : 0, null, monster, monster?.Name ?? "monster");
        }

        public static HpOwnerRef NonUnit(uint entityId, string debugName)
        {
            return new HpOwnerRef(HpOwnerKind.NonUnit, entityId, null, null, debugName ?? "non-unit");
        }

        public static HpOwnerRef Unknown(uint entityId, string debugName)
        {
            return new HpOwnerRef(HpOwnerKind.Unknown, entityId, null, null, debugName ?? "unknown");
        }
    }

    public readonly struct HpReportDecision
    {
        public readonly bool Accepted;
        public readonly string Reason;

        private HpReportDecision(bool accepted, string reason)
        {
            Accepted = accepted;
            Reason = reason;
        }

        public static HpReportDecision AcceptObserved(string reason)
        {
            return new HpReportDecision(true, reason ?? "accepted");
        }

        public static HpReportDecision Reject(string reason)
        {
            return new HpReportDecision(false, reason ?? "rejected");
        }
    }

    public readonly struct HpSynchResolveResult
    {
        public readonly bool AllowPacket;
        public readonly EntitySynchInfoPayload Payload;
        public readonly HpOwnerKind OwnerKind;
        public readonly uint OwnerEntityId;
        public readonly string Reason;

        public bool HasHP => Payload.HasHP;

        private HpSynchResolveResult(bool allowPacket, EntitySynchInfoPayload payload, HpOwnerKind ownerKind, uint ownerEntityId, string reason)
        {
            AllowPacket = allowPacket;
            Payload = payload;
            OwnerKind = ownerKind;
            OwnerEntityId = ownerEntityId;
            Reason = reason;
        }

        public static HpSynchResolveResult AllowEmpty(HpOwnerKind ownerKind, uint ownerEntityId, string reason)
        {
            return new HpSynchResolveResult(true, EntitySynchInfoPayload.Empty, ownerKind, ownerEntityId, reason ?? "empty");
        }

        public static HpSynchResolveResult AllowHP(HpOwnerKind ownerKind, uint ownerEntityId, uint hpWire, string reason)
        {
            return new HpSynchResolveResult(true, EntitySynchInfoPayload.FromHP(hpWire), ownerKind, ownerEntityId, reason ?? "hp");
        }

        public static HpSynchResolveResult Block(HpOwnerKind ownerKind, uint ownerEntityId, string reason)
        {
            return new HpSynchResolveResult(false, EntitySynchInfoPayload.Empty, ownerKind, ownerEntityId, reason ?? "blocked");
        }
    }
}
