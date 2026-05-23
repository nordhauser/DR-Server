using System;
using System.Collections.Generic;
using DungeonRunners.Combat;
using DungeonRunners.Networking;
using UnityEngine;

namespace DungeonRunners.Networking.Sync
{
    public sealed class HpSyncService
    {
        public static HpSyncService Instance { get; } = new HpSyncService();

        public const uint WirePerHP = 256u;
        public const uint ClientHpMaxToleranceWire = 5u * WirePerHP;
        private const double NativeTickSeconds = 1d / 30d;
        private const ushort NativeDamageRegenCooldownTicks = 300;

        private readonly Dictionary<uint, HpAuthorityState> _byEntity = new Dictionary<uint, HpAuthorityState>();
        private readonly Dictionary<uint, HpAuthorityState> _playerByConn = new Dictionary<uint, HpAuthorityState>();
        private readonly Dictionary<ushort, uint> _componentOwnerEntity = new Dictionary<ushort, uint>();
        private readonly Dictionary<ushort, HpOwnerKind> _componentKind = new Dictionary<ushort, HpOwnerKind>();
        private readonly Dictionary<uint, RRConnection> _playerConnections = new Dictionary<uint, RRConnection>();
        private readonly Dictionary<uint, Monster> _monsters = new Dictionary<uint, Monster>();

        private HpSyncService()
        {
        }

        public HpAuthorityState RegisterPlayer(RRConnection conn, PlayerState playerState, uint avatarEntityId)
        {
            return RegisterPlayer(conn, playerState, avatarEntityId, null, true);
        }

        private HpAuthorityState RegisterPlayer(RRConnection conn, PlayerState playerState, uint avatarEntityId, float? nativeTime, bool allowWallClockRegenInit)
        {
            if (conn == null || playerState == null || avatarEntityId == 0) return null;
            HpAuthorityState s = GetOrCreate(avatarEntityId, HpOwnerKind.PlayerAvatar);
            s.OwnerKind = HpOwnerKind.PlayerAvatar;
            s.ConnectionId = (uint)Math.Max(0, conn.ConnId);
            s.DebugName = conn.LoginName ?? conn.ConnId.ToString();
            s.MaxHPWire = playerState.MaxHPWire;
            uint rawHP = playerState.CurrentHPWire;
            s.RuntimeHPWire = s.MaxHPWire > 0 ? Math.Min(rawHP, s.MaxHPWire) : rawHP;
            s.IsAlive = s.RuntimeHPWire > 0;
            if (s.LastRegenTime < 0f)
            {
                if (nativeTime.HasValue)
                    s.LastRegenTime = nativeTime.Value;
                else if (allowWallClockRegenInit)
                    s.LastRegenTime = Time.time;
            }
            if (conn.IgnoreClientHPUntilTime > s.IgnoreClientReportsUntil)
                s.IgnoreClientReportsUntil = conn.IgnoreClientHPUntilTime;
            _playerByConn[(uint)Math.Max(0, conn.ConnId)] = s;
            _playerConnections[avatarEntityId] = conn;
            RegisterPlayerComponents(conn, avatarEntityId);
            MirrorPlayerDebug(conn, playerState, s);
            return s;
        }

        public HpAuthorityState RegisterMonster(Monster monster)
        {
            if (monster == null || monster.EntityId == 0) return null;
            bool existed = _byEntity.TryGetValue(monster.EntityId, out HpAuthorityState s);
            if (!existed)
                s = GetOrCreate(monster.EntityId, HpOwnerKind.Monster);
            s.OwnerKind = HpOwnerKind.Monster;
            s.DebugName = monster.Name ?? monster.EntityId.ToString();
            s.MaxHPWire = monster.MaxHPWire;
            uint rawHP = monster.IsAlive ? monster.CurrentHPWire : 0;
            uint clampedRaw = s.MaxHPWire > 0 ? Math.Min(rawHP, s.MaxHPWire) : rawHP;
            if (!existed)
            {
                s.RuntimeHPWire = clampedRaw;
            }
            else if (!monster.IsAlive)
            {
                s.RuntimeHPWire = 0;
            }
            else if (s.RuntimeHPWire > 0 && clampedRaw > 0)
            {
                s.RuntimeHPWire = Math.Min(s.ClampHP(s.RuntimeHPWire), clampedRaw);
            }
            else
            {
                s.RuntimeHPWire = s.ClampHP(s.RuntimeHPWire);
            }
            s.IsAlive = monster.IsAlive && s.RuntimeHPWire > 0;
            if (s.LastRegenTime < 0f) s.LastRegenTime = Time.time;
            _monsters[monster.EntityId] = monster;
            RegisterMonsterComponents(monster);
            MirrorToMonster(monster, s);
            return s;
        }

        public void UnregisterPlayer(RRConnection conn)
        {
            if (conn == null) return;
            uint avatarEntityId = conn.Avatar != null ? (uint)conn.Avatar.Id : 0;
            _playerByConn.Remove((uint)Math.Max(0, conn.ConnId));
            if (avatarEntityId != 0)
                _playerConnections.Remove(avatarEntityId);
            UnregisterPlayerComponents(conn, avatarEntityId);
        }

        public void UnregisterMonster(uint monsterEntityId)
        {
            if (monsterEntityId == 0) return;
            _byEntity.Remove(monsterEntityId);
            _monsters.Remove(monsterEntityId);
            RemoveComponentOwner(monsterEntityId);
        }

        public HpAuthorityState GetPlayerState(RRConnection conn, PlayerState playerState, uint avatarEntityId)
        {
            return RegisterPlayer(conn, playerState, avatarEntityId);
        }

        public HpAuthorityState GetMonsterState(Monster monster)
        {
            return RegisterMonster(monster);
        }

        public bool TryResolvePlayerOwner(RRConnection conn, PlayerState playerState, uint avatarEntityId, out HpOwnerRef owner)
        {
            owner = default;
            if (RegisterPlayer(conn, playerState, avatarEntityId) == null) return false;
            owner = HpOwnerRef.Player(avatarEntityId, conn);
            return true;
        }

        public bool TryResolveMonsterOwner(Monster monster, out HpOwnerRef owner)
        {
            owner = default;
            if (RegisterMonster(monster) == null) return false;
            owner = HpOwnerRef.MonsterOwner(monster);
            return true;
        }

        public bool TryResolveComponentOwner(RRConnection conn, ushort componentId, uint entityId, out HpOwnerRef owner)
        {
            owner = default;
            if (componentId != 0 && _componentKind.TryGetValue(componentId, out HpOwnerKind kind) && kind == HpOwnerKind.NonUnit)
            {
                owner = HpOwnerRef.NonUnit(componentId, "player-empty-component");
                return true;
            }
            if (componentId != 0 && _componentOwnerEntity.TryGetValue(componentId, out uint ownerEntity))
            {
                if (_byEntity.TryGetValue(ownerEntity, out HpAuthorityState state))
                {
                    if (state.OwnerKind == HpOwnerKind.PlayerAvatar)
                    {
                        _playerConnections.TryGetValue(ownerEntity, out RRConnection playerConn);
                        owner = HpOwnerRef.Player(ownerEntity, playerConn ?? conn);
                        return true;
                    }
                    if (state.OwnerKind == HpOwnerKind.Monster)
                    {
                        _monsters.TryGetValue(ownerEntity, out Monster monster);
                        owner = new HpOwnerRef(HpOwnerKind.Monster, ownerEntity, null, monster, state.DebugName);
                        return true;
                    }
                }
            }
            if (entityId != 0 && _byEntity.TryGetValue(entityId, out HpAuthorityState entityState))
            {
                if (entityState.OwnerKind == HpOwnerKind.PlayerAvatar)
                {
                    _playerConnections.TryGetValue(entityId, out RRConnection playerConn);
                    owner = HpOwnerRef.Player(entityId, playerConn ?? conn);
                    return true;
                }
                if (entityState.OwnerKind == HpOwnerKind.Monster)
                {
                    _monsters.TryGetValue(entityId, out Monster monster);
                    owner = new HpOwnerRef(HpOwnerKind.Monster, entityId, null, monster, entityState.DebugName);
                    return true;
                }
            }
            return false;
        }

        public HpSynchResolveResult ResolveOutboundPlayer(RRConnection conn, PlayerState playerState, uint avatarEntityId, SyncContext context, string packetName, bool advanceClientSync, out uint hpWire)
        {
            return ResolveOutboundPlayerCore(conn, playerState, avatarEntityId, context, packetName, advanceClientSync, null, out hpWire);
        }

        public HpSynchResolveResult ResolveOutboundPlayer(RRConnection conn, PlayerState playerState, uint avatarEntityId, SyncContext context, string packetName, bool advanceClientSync, float nativeTime, out uint hpWire)
        {
            return ResolveOutboundPlayerCore(conn, playerState, avatarEntityId, context, packetName, advanceClientSync, nativeTime, out hpWire);
        }

        private HpSynchResolveResult ResolveOutboundPlayerCore(RRConnection conn, PlayerState playerState, uint avatarEntityId, SyncContext context, string packetName, bool advanceClientSync, float? nativeTime, out uint hpWire)
        {
            hpWire = 0;
            HpAuthorityState s = RegisterPlayer(conn, playerState, avatarEntityId, nativeTime, false);
            if (s == null)
                return HpSynchResolveResult.Block(HpOwnerKind.PlayerAvatar, avatarEntityId, "missing-player-state");
            if (advanceClientSync && nativeTime.HasValue)
            {
                playerState.AdvanceClientSyncHP(nativeTime.Value, packetName ?? "HpSyncService");
                SyncPlayerKnownFromDomain(playerState, s);
            }
            hpWire = playerState.SynchHP;
            MarkOutbound(s, hpWire, packetName, nativeTime);
            MirrorPlayerDebug(conn, playerState, s);
            return HpSynchResolveResult.AllowHP(HpOwnerKind.PlayerAvatar, avatarEntityId, hpWire, "client-visible-hp");
        }

        public HpSynchResolveResult ResolveOutboundMonster(Monster monster, SyncContext context, string packetName, out uint hpWire)
        {
            hpWire = 0;
            HpAuthorityState s = RegisterMonster(monster);
            if (s == null)
                return HpSynchResolveResult.Block(HpOwnerKind.Monster, monster != null ? monster.EntityId : 0, "missing-monster-state");
            hpWire = s.ClampHP(s.RuntimeHPWire);
            MarkOutbound(s, hpWire, packetName);
            MirrorToMonster(monster, s);
            return HpSynchResolveResult.AllowHP(HpOwnerKind.Monster, monster.EntityId, hpWire, "client-visible-hp");
        }

        public bool ObserveClientHpReport(HpOwnerRef owner, uint hpWire, HpReportSource source, string packetName, bool reportCameFromEntitySynchInfo, out HpReportDecision decision)
        {
            decision = default;
            if (!_byEntity.TryGetValue(owner.EntityId, out HpAuthorityState s))
            {
                decision = HpReportDecision.Reject("missing-state");
                return false;
            }
            if (Time.time < s.IgnoreClientReportsUntil)
            {
                RememberReject(s, hpWire, "ignore-window");
                decision = HpReportDecision.Reject("ignore-window");
                return false;
            }
            if (s.MaxHPWire > 0 && hpWire > s.MaxHPWire + ClientHpMaxToleranceWire)
            {
                RememberReject(s, hpWire, "above-max+tolerance");
                decision = HpReportDecision.Reject("above-max+tolerance");
                return false;
            }
            uint clamped = s.ClampHP(hpWire);
            RecordObservedClientHP(s, clamped, source.ToString());
            decision = HpReportDecision.AcceptObserved("observed-only");
            MirrorOwner(owner, s);
            return true;
        }

        public void RecordPlayerOutboundHP(RRConnection conn, PlayerState playerState, uint avatarEntityId, uint hpWire, string source)
        {
            HpAuthorityState s = RegisterPlayer(conn, playerState, avatarEntityId);
            if (s == null) return;
            MarkOutbound(s, s.ClampHP(hpWire), source ?? "outbound");
            MirrorPlayerDebug(conn, playerState, s);
        }

        public void RecordMonsterOutboundHP(Monster monster, uint hpWire, string source)
        {
            HpAuthorityState s = RegisterMonster(monster);
            if (s == null) return;
            uint clamped = s.ClampHP(hpWire);
            MarkOutbound(s, clamped, source ?? "outbound");
            MirrorToMonster(monster, s);
        }

        public void SetRuntimeHP(HpAuthorityState s, uint hpWire, string reason)
        {
            SetRuntimeHPInternal(s, hpWire, reason);
        }

        public void ApplyDamage(HpAuthorityState s, uint damageWire, string reason)
        {
            ApplyDamage(s, damageWire, reason, null);
        }

        public void ApplyDamage(HpAuthorityState s, uint damageWire, string reason, float nativeDamageTime)
        {
            ApplyDamage(s, damageWire, reason, (float?)nativeDamageTime);
        }

        private void ApplyDamage(HpAuthorityState s, uint damageWire, string reason, float? nativeDamageTime)
        {
            if (s == null || damageWire == 0) return;
            uint old = s.RuntimeHPWire;
            uint next = damageWire >= old ? 0 : old - damageWire;
            SetRuntimeHPInternal(s, next, reason ?? "damage");
            s.RegenCooldownTicks = NativeDamageRegenCooldownTicks;
            s.RegenCarrySeconds = 0d;
            if (nativeDamageTime.HasValue)
                s.LastRegenTime = nativeDamageTime.Value;
        }

        public void ApplyHeal(HpAuthorityState s, uint amountWire, string reason, bool visibleImmediately)
        {
            if (s == null || amountWire == 0) return;
            uint old = s.RuntimeHPWire;
            uint next = s.ClampHP(old + amountWire);
            SetRuntimeHPInternal(s, next, reason ?? "heal");
        }

        public void SetMaxHP(HpAuthorityState s, uint newMaxHPWire, HpMaxChangeMode mode, string reason)
        {
            if (s == null) return;
            uint oldMax = s.MaxHPWire;
            uint oldRuntime = s.RuntimeHPWire;
            s.MaxHPWire = newMaxHPWire;
            if (mode == HpMaxChangeMode.FillToMax)
                s.RuntimeHPWire = newMaxHPWire;
            else if (mode == HpMaxChangeMode.PreservePercent && oldMax > 0)
            {
                ulong scaled = (ulong)oldRuntime * newMaxHPWire / oldMax;
                s.RuntimeHPWire = (uint)Math.Min((ulong)newMaxHPWire, scaled);
            }
            else if (mode == HpMaxChangeMode.AddDeltaToCurrent && newMaxHPWire > oldMax)
                s.RuntimeHPWire = Math.Min(newMaxHPWire, oldRuntime + (newMaxHPWire - oldMax));
            else
                s.RuntimeHPWire = Math.Min(oldRuntime, newMaxHPWire);
        }

        public void AdvanceNativeRegen(HpAuthorityState s, float now, bool allowObservedAdvance)
        {
            if (s == null) return;
            if (!s.RegenEnabled || s.RegenFactor <= 0 || s.MaxHPWire == 0 || !s.IsAlive)
            {
                s.LastRegenTime = now;
                s.RegenCarrySeconds = 0d;
                return;
            }
            if (s.LastRegenTime < 0f)
            {
                s.LastRegenTime = now;
                return;
            }
            if (s.RuntimeHPWire >= s.MaxHPWire)
            {
                s.LastRegenTime = now;
                s.RegenCarrySeconds = 0d;
                return;
            }
            double elapsed = (now - s.LastRegenTime) + s.RegenCarrySeconds;
            int ticks = (int)(elapsed / NativeTickSeconds);
            if (ticks <= 0)
            {
                s.RegenCarrySeconds = elapsed;
                s.LastRegenTime = now;
                return;
            }
            uint before = s.RuntimeHPWire;
            for (int i = 0; i < ticks && s.RuntimeHPWire < s.MaxHPWire; i++)
            {
                if (s.RegenCooldownTicks > 0)
                {
                    s.RegenCooldownTicks--;
                    if (s.RegenCooldownTicks > 0)
                        continue;
                }
                uint delta = (uint)(((long)s.RegenFactor * s.MaxHPWire) / 3000L + 1L);
                s.RuntimeHPWire = s.ClampHP(s.RuntimeHPWire + delta);
            }
            s.RegenCarrySeconds = elapsed - ticks * NativeTickSeconds;
            s.LastRegenTime = now;
        }

        public static HpReportSource ClassifyReportSource(string source)
        {
            if (string.IsNullOrEmpty(source)) return HpReportSource.Unknown;
            if (source.StartsWith("DLL-HP", StringComparison.Ordinal)) return HpReportSource.ClientDllHpHook;
            if (source.StartsWith("HP-SYNC", StringComparison.Ordinal)) return HpReportSource.ClientEntitySynchSuffix;
            if (source.StartsWith("SEND-UPDATE", StringComparison.Ordinal)) return HpReportSource.ClientSendUpdate;
            if (source.StartsWith("PLAYER-STATE-SYNC", StringComparison.Ordinal)) return HpReportSource.ClientPlayerStateSync;
            if (source.StartsWith("ACTION-0x50-SYNC", StringComparison.Ordinal)) return HpReportSource.ClientEntitySynchSuffix;
            if (source.StartsWith("MONSTER-MOVE-HP", StringComparison.Ordinal)) return HpReportSource.ClientEntitySynchSuffix;
            if (source.StartsWith("MONSTER-SM-HP", StringComparison.Ordinal)) return HpReportSource.ClientEntitySynchSuffix;
            if (source.StartsWith("ENTITY-SYNC", StringComparison.Ordinal)) return HpReportSource.ClientEntitySynchSuffix;
            return HpReportSource.Unknown;
        }

        private HpAuthorityState GetOrCreate(uint entityId, HpOwnerKind kind)
        {
            if (!_byEntity.TryGetValue(entityId, out HpAuthorityState s))
            {
                s = new HpAuthorityState { OwnerEntityId = entityId, OwnerKind = kind };
                _byEntity[entityId] = s;
            }
            return s;
        }

        private void RegisterPlayerComponents(RRConnection conn, uint avatarEntityId)
        {
            RegisterComponent((ushort)avatarEntityId, avatarEntityId, HpOwnerKind.PlayerAvatar);
            RegisterComponent((ushort)conn.UnitBehaviorId, avatarEntityId, HpOwnerKind.PlayerAvatar);
            RegisterComponent(conn.BehaviorComponentId, avatarEntityId, HpOwnerKind.PlayerAvatar);
            RegisterComponent(conn.SkillsComponentId, avatarEntityId, HpOwnerKind.PlayerAvatar);
            RegisterComponent(conn.ManipulatorsComponentId, avatarEntityId, HpOwnerKind.PlayerAvatar);
            RegisterComponent((ushort)conn.ModifiersId, avatarEntityId, HpOwnerKind.PlayerAvatar);
            RegisterComponent(conn.ModifiersComponentId, avatarEntityId, HpOwnerKind.PlayerAvatar);
            RegisterComponent((ushort)conn.UnitContainerId, avatarEntityId, HpOwnerKind.PlayerAvatar);
            RegisterComponent((ushort)conn.DialogManagerId, avatarEntityId, HpOwnerKind.NonUnit);
            RegisterComponent((ushort)conn.QuestManagerId, avatarEntityId, HpOwnerKind.NonUnit);
        }

        private void UnregisterPlayerComponents(RRConnection conn, uint avatarEntityId)
        {
            if (conn == null) return;
            RemoveComponent((ushort)avatarEntityId);
            RemoveComponent((ushort)conn.UnitBehaviorId);
            RemoveComponent(conn.BehaviorComponentId);
            RemoveComponent(conn.SkillsComponentId);
            RemoveComponent(conn.ManipulatorsComponentId);
            RemoveComponent((ushort)conn.ModifiersId);
            RemoveComponent(conn.ModifiersComponentId);
            RemoveComponent((ushort)conn.UnitContainerId);
            RemoveComponent((ushort)conn.DialogManagerId);
            RemoveComponent((ushort)conn.QuestManagerId);
        }

        private void RegisterMonsterComponents(Monster monster)
        {
            if (monster == null) return;
            RegisterComponent((ushort)monster.EntityId, monster.EntityId, HpOwnerKind.Monster);
            RegisterComponent((ushort)monster.BehaviorId, monster.EntityId, HpOwnerKind.Monster);
            RegisterComponent((ushort)monster.SkillsId, monster.EntityId, HpOwnerKind.Monster);
            RegisterComponent((ushort)monster.ManipulatorsId, monster.EntityId, HpOwnerKind.Monster);
            RegisterComponent((ushort)monster.ModifiersId, monster.EntityId, HpOwnerKind.Monster);
            RegisterComponent((ushort)monster.UnitId, monster.EntityId, HpOwnerKind.Monster);
        }

        private void RegisterComponent(ushort componentId, uint ownerEntityId, HpOwnerKind kind)
        {
            if (componentId == 0 || ownerEntityId == 0) return;
            _componentOwnerEntity[componentId] = ownerEntityId;
            _componentKind[componentId] = kind;
        }

        private void RemoveComponent(ushort componentId)
        {
            if (componentId == 0) return;
            _componentOwnerEntity.Remove(componentId);
            _componentKind.Remove(componentId);
        }

        private void RemoveComponentOwner(uint ownerEntityId)
        {
            if (ownerEntityId == 0) return;
            List<ushort> remove = new List<ushort>();
            foreach (var kvp in _componentOwnerEntity)
            {
                if (kvp.Value == ownerEntityId)
                    remove.Add(kvp.Key);
            }
            foreach (ushort componentId in remove)
                RemoveComponent(componentId);
        }

        private void SetRuntimeHPInternal(HpAuthorityState s, uint hpWire, string reason)
        {
            if (s == null) return;
            uint old = s.RuntimeHPWire;
            uint next = s.ClampHP(hpWire);
            s.RuntimeHPWire = next;
            s.IsAlive = next > 0;
        }

        private void RecordObservedClientHP(HpAuthorityState s, uint hpWire, string source)
        {
            uint clamped = s.ClampHP(hpWire);
            s.LastObservedClientHPWire = clamped;
            s.LastObservedClientHPTime = Time.time;
            s.LastObservedClientHPSource = source ?? "unknown";
        }

        private void RememberReject(HpAuthorityState s, uint hpWire, string reason)
        {
            s.LastRejectedClientHPWire = hpWire;
            s.LastRejectedClientHPTime = Time.time;
            s.LastRejectedClientHPReason = reason ?? "rejected";
        }

        private void SyncPlayerKnownFromDomain(PlayerState playerState, HpAuthorityState s)
        {
            if (playerState == null || s == null) return;
            s.MaxHPWire = playerState.MaxHPWire;
            uint rawHP = playerState.CurrentHPWire;
            s.RuntimeHPWire = s.MaxHPWire > 0 ? Math.Min(rawHP, s.MaxHPWire) : rawHP;
            s.IsAlive = s.RuntimeHPWire > 0;
        }

        private void MirrorPlayerDebug(RRConnection conn, PlayerState playerState, HpAuthorityState s)
        {
            if (conn == null || s == null) return;
            conn.LastOutboundHPWire = s.LastOutboundHPWire;
            conn.LastOutboundHPTime = s.LastOutboundHPTime;
            conn.LastOutboundHPSource = s.LastOutboundPacket;
        }

        private void MirrorToMonster(Monster monster, HpAuthorityState s)
        {
            if (monster == null || s == null) return;
            monster.CurrentHPWire = s.RuntimeHPWire;
            monster.LastClientHPReportTime = s.LastObservedClientHPTime;
            monster.LastClientHPReportWire = s.LastObservedClientHPWire;
        }

        private void MirrorOwner(HpOwnerRef owner, HpAuthorityState s)
        {
            if (owner.Kind == HpOwnerKind.PlayerAvatar && owner.Connection != null)
                MirrorPlayerDebug(owner.Connection, null, s);
            if (owner.Kind == HpOwnerKind.Monster && owner.Monster != null)
                MirrorToMonster(owner.Monster, s);
        }

        private void MarkOutbound(HpAuthorityState s, uint hpWire, string packetName, float? nativeTime = null)
        {
            s.LastOutboundHPWire = hpWire;
            s.LastOutboundHPTime = nativeTime ?? Time.time;
            s.LastOutboundPacket = packetName ?? "unknown";
            s.LastOutboundFlags = 0x02;
        }

        private bool IsHpIncreaseSource(HpReportSource source)
        {
            return source == HpReportSource.ServerPotionHeal
                || source == HpReportSource.ServerSkillHeal
                || source == HpReportSource.ServerNativeRegen
                || source == HpReportSource.ServerEquipmentChange
                || source == HpReportSource.ServerStatAllocation
                || source == HpReportSource.ServerLevelUp
                || source == HpReportSource.ServerRespawn
                || source == HpReportSource.ServerZoneLoad
                || source == HpReportSource.PersistenceLoad;
        }

        private bool IsWithinTolerance(uint a, uint b, uint tolerance)
        {
            return a >= b ? a - b <= tolerance : b - a <= tolerance;
        }
    }
}
