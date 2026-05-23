using System;

namespace DungeonRunners.Networking.Sync
{
    public sealed class HpAuthorityState
    {
        public HpOwnerKind OwnerKind;
        public uint OwnerEntityId;
        public uint ConnectionId;
        public string DebugName;

        public uint MaxHPWire;
        public uint RuntimeHPWire;
        public bool IsAlive = true;

        public int RegenFactor;
        public ushort RegenCooldownTicks;
        public float LastRegenTime = -1f;
        public double RegenCarrySeconds;
        public bool RegenEnabled = true;

        public uint LastObservedClientHPWire;
        public float LastObservedClientHPTime;
        public string LastObservedClientHPSource;
        public uint LastRejectedClientHPWire;
        public float LastRejectedClientHPTime;
        public string LastRejectedClientHPReason;

        public uint LastOutboundHPWire;
        public float LastOutboundHPTime;
        public string LastOutboundPacket;
        public byte LastOutboundFlags;

        public float IgnoreClientReportsUntil;
        public float InvulnerableUntil;

        public uint ClampHP(uint hp)
        {
            return MaxHPWire == 0 ? 0 : Math.Min(hp, MaxHPWire);
        }
    }
}
