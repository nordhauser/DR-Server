using DungeonRunners.Utilities;

namespace DungeonRunners.Networking.Sync
{
    public readonly struct EntitySynchInfoPayload
    {
        public readonly byte Flags;
        public readonly uint HPWire;

        public bool HasHP => (Flags & 0x02) != 0;

        public EntitySynchInfoPayload(byte flags, uint hpWire)
        {
            Flags = flags;
            HPWire = hpWire;
        }

        public static EntitySynchInfoPayload Empty => new EntitySynchInfoPayload(0x00, 0);

        public static EntitySynchInfoPayload FromHP(uint hpWire)
        {
            return new EntitySynchInfoPayload(0x02, hpWire);
        }

        public void Write(LEWriter writer)
        {
            writer.WriteByte(Flags);
            if ((Flags & 0x02) != 0)
                writer.WriteUInt32(HPWire);
        }

        public override string ToString()
        {
            return HasHP ? $"flags=0x{Flags:X2} hp={HPWire} hpFloat={HPWire / 256f:F2}" : $"flags=0x{Flags:X2}";
        }
    }

    public readonly struct ResolvedEntitySynchInfo
    {
        public readonly EntitySynchInfoPayload Payload;
        public readonly uint OwnerEntityId;
        public readonly uint ComponentId;
        public readonly byte Subtype;
        public readonly float NativeNow;
        public readonly uint ValidationCutoffTick;
        public readonly float ValidationCutoffTime;
        public readonly string Reason;
        public readonly string Provenance;

        public byte Flags => Payload.Flags;
        public uint HPWire => Payload.HPWire;
        public bool HasHP => Payload.HasHP;

        public ResolvedEntitySynchInfo(EntitySynchInfoPayload payload, uint ownerEntityId, uint componentId, byte subtype, float nativeNow, string reason, string provenance, uint validationCutoffTick = 0, float validationCutoffTime = -1f)
        {
            Payload = payload;
            OwnerEntityId = ownerEntityId;
            ComponentId = componentId;
            Subtype = subtype;
            NativeNow = nativeNow;
            ValidationCutoffTick = validationCutoffTick;
            ValidationCutoffTime = validationCutoffTime;
            Reason = reason ?? "resolved";
            Provenance = provenance ?? "runtime";
        }

        public static ResolvedEntitySynchInfo FromHP(uint ownerEntityId, uint componentId, byte subtype, uint hpWire, float nativeNow, string reason, string provenance)
        {
            return new ResolvedEntitySynchInfo(EntitySynchInfoPayload.FromHP(hpWire), ownerEntityId, componentId, subtype, nativeNow, reason, provenance);
        }

        public override string ToString()
        {
            return $"{Payload} owner={OwnerEntityId} component={ComponentId} sub=0x{Subtype:X2} nativeNow={NativeNow:F3} cutoffTick={ValidationCutoffTick} cutoffTime={ValidationCutoffTime:F3} reason={Reason} provenance={Provenance}";
        }
    }
}
