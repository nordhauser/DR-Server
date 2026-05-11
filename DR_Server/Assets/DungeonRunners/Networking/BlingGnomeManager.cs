using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using DungeonRunners.Utilities;
using DungeonRunners.Core;
using DungeonRunners.Data;

namespace DungeonRunners.Networking
{
    /// <summary>
    /// BlingGnome henchman manager — server-driven architecture.
    /// Based on verified Go reference implementation.
    /// 
    /// Architecture: ALL behavior driven server-side. The client's native
    /// BlingGnomeBehavior SM handles state transitions internally (state 0 → 5),
    /// but follow/search/pickup/fidget/convert are all server-driven via
    /// mover updates (0x65) and PlayAnimation actions.
    /// 
    /// Flow:
    /// 1. Entity snapshot (Op 0x01 + 0x02) — creates the 3D model
        /// 2. Delayed behavior bootstrap (~66ms) — Op 0x32 creates Modifiers,
    ///    Manipulators, BlingGnomeBehavior with proper SM2 init
    /// 3. Server AI loop drives follow/search/pickup/fidget via packets
    /// </summary>
    public class BlingGnomeManager : MonoBehaviour
    {
        private static BlingGnomeManager _instance;
        public static BlingGnomeManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    GameObject go = new GameObject("BlingGnomeManager");
                    _instance = go.AddComponent<BlingGnomeManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // PER-PLAYER GNOME STATE
        // ═══════════════════════════════════════════════════════════════════
        private class GnomeState
        {
            // Entity IDs (all allocated sequentially)
            public ushort EntityId;
            public ushort BehaviorId;
            public ushort ModifiersId;
            public ushort ManipulatorsId;

            // Position (world coords, NOT fixed-point)
            public float PosX, PosY, PosZ;
            public float Heading;

            // Timing
            public float SpawnTime;
            public float ActivateUntil;        // conversion window end time (Time.time)

            // Stats
            public int ItemsConverted;
            public uint GoldGenerated;
            public uint HitPointsWire;

            // State
            public bool BehaviorBootstrapped;   // behavior component created
            public bool IsActive;               // conversion window open
            public int FidgetSerial;            // for animation variety
            public ushort LastFidgetAnim;       // avoid repeats
            public float NextFidgetTime;        // next fidget due
            public float NextSearchTime;        // next item search due
            public float SpawnPosX, SpawnPosY;
            public float SpawnHeading;
            public bool HasMoveTarget;
            public float MoveTargetX, MoveTargetY;
            public float LastFollowRuntimeAt;
            public bool LastMoverSyncValid;
            public float LastMoverSyncAt;
            public float LastMoverSyncPosX, LastMoverSyncPosY;
            public float LastMoverSyncHeading;
            public bool LastMoverSyncTerminal;

            // Infrastructure
            public Coroutine AICoroutine;
            public Coroutine ConversionCoroutine;
            public SendPacketDelegate SendPacket;
            public SendMessageDelegate SendMessage;
        }

        private Dictionary<int, GnomeState> _gnomes = new Dictionary<int, GnomeState>();
        private uint _nextEntityId = 0x6000;

        // ═══════════════════════════════════════════════════════════════════
        // CONSTANTS — from Go reference (worldBlingGnomeAuthored*)
        // ═══════════════════════════════════════════════════════════════════

        // GC types
        private const string ENTITY_GC_TYPE = "creatures.summon.blinggnome.base.BlingGnome_Summon";
        private const string BEHAVIOR_GC_TYPE = "creatures.summon.blinggnome.base.BlingGnome_Summon.Behavior";
        private const string SKILL_TYPE = "skills.generic.SummonBlingGnome";

        // Behavior constants (from Go: worldBlingGnomeAuthored*)
        private const float CONVERSION_RATIO = 0.5f;
        private const float GOLD_VALUE_MOD = 2.3f;
        private const int CONVERT_SEARCH_RADIUS = 200;
        private const int BEHAVIOR_SEARCH_RANGE = 250;
        private const int GNOME_SPEED = 50;
        private const float COOLDOWN_SECONDS = 45f;
        private const float EFFECT_DURATION_SECONDS = 10f;
        private const int IDLE_BASE_SECONDS = 2;
        private const int IDLE_VARIABLE_SECONDS = 7;

        // Follow behavior
        private const float FOLLOW_START_RADIUS = 30f;    // start following when > this from avatar
        private const float FOLLOW_SETTLE_RADIUS = 30f;   // stop following when < this
        private const float FOLLOW_TOO_CLOSE = 10f;       // reposition if < this
        private const float FOLLOW_RETARGET_DEADBAND = 20f;
        private const float FOLLOW_MAX_STEP_CADENCE = 0.132f;
        private const float FOLLOW_DEFAULT_STEP_CADENCE = 0.099f;
        private const float FOLLOW_STEP_MIN_INTERVAL = 0.033f;
        private const float MOVER_SYNC_ACTIVE_MIN_INTERVAL = 0.300f;
        private const float MOVER_SYNC_SETTLED_MIN_INTERVAL = 0.500f;
        private const float MOVER_SYNC_ACTIVE_MAX_DRIFT = 14f;
        private const float MOVER_SYNC_SETTLED_MAX_DRIFT = 18f;
        private const float LEASH_SNAP_RADIUS = 480f;     // teleport if > this

        // Timing
        private const float AI_TICK_INTERVAL = 0.15f;     // ~150ms per Go worldIntervalCadence
        private const float SEARCH_PULSE_SECONDS = 1.0f;  // ~30 ticks at 30fps
        private const float BEHAVIOR_BOOTSTRAP_DELAY = 0.066f; // 66ms delay before behavior create
        private const float DESPAWN_SECONDS = 120f;

        // Animation logical IDs (from Go constants)
        private const ushort ANIM_IDLE = 100;
        private const ushort ANIM_FIDGET_FIRST = 101;
        private const ushort ANIM_FIDGET_LAST = 104;
        private const ushort ANIM_PICKUP = 110;
        private const ushort ANIM_ACTIVE_SKILL = 140;
        private const ushort ANIM_DEATH = 150;
        private const ushort ANIM_SPAWN = 180;

        // Animation states (from Go constants)
        private const byte ANIM_STATE_DIRECT = 0x00;
        private const byte ANIM_STATE_ACTIVE = 0x06;
        private const byte ANIM_STATE_FIDGET = 0x08;

        // Sound IDs
        private const ushort SOUND_FIDGET = 111;
        private const int FIDGET_SOUND_CHANCE_DIVISOR = 3;  // 1 in 3 chance

        private const float BLING_HEALTH_SCALE = 0.3f;
        private static readonly int[] HENCHMAN_HEALTH_LEVELS = { 1, 5, 10, 50, 75, 100, 110 };
        private static readonly float[] HENCHMAN_HEALTH_VALUES = { 245f, 1550f, 2810f, 10989f, 19646f, 26009f, 28020f };

        private const int ENTITY_INIT_MODE = 2;

        /// <summary>
        /// Compute deterministic NPC component ID from entity ID + slot.
        /// EXACT match for Go's worldNPCComponentIDFromEntityID — the client's
        /// BlingGnomeBehavior uses this formula to find its sibling components
        /// (Modifiers slot=5, Manipulators slot=6, UnitBehavior slot=7).
        /// Without this, lookups fail and behavior crashes at owner-scene resolution.
        /// </summary>
        private static ushort ComputeNPCComponentId(ushort entityId, ushort slot)
        {
            if (entityId == 0) return slot;
            uint id = ((uint)entityId & 0xFF00u) + 0x0100u + ((uint)entityId & 0x00FFu) * 0x10u + slot;
            if (id <= 0xFFFF) return (ushort)id;
            return (ushort)(entityId + 0x0100 + slot);
        }

        // DIAGNOSTIC: Component bootstrap test mode
        // 0 = no components (entity only - CONFIRMED WORKING)
        // 1 = Modifiers only
        // 2 = Modifiers + Manipulators
        // 3 = Modifiers + Manipulators + BlingGnomeBehavior (full bootstrap)
        // Test mode 3 (with behavior) to verify if entity init slot fix resolves owner-scene crash
        private const int COMPONENT_TEST_MODE = 3;

        private UnityGameServer _server;
        private System.Random _rng = new System.Random();
        private static readonly bool SendServerGnomeMoverUpdates = false;

        public delegate void SendPacketDelegate(RRConnection conn, byte dest, byte type, byte[] data);
        public delegate void SendMessageDelegate(RRConnection conn, string message);

        public void SetServer(UnityGameServer server) => _server = server;
        public void SetNextEntityId(uint id) => _nextEntityId = id;
        public uint GetNextEntityId() => _nextEntityId++;
        public bool HasGnome(int connId) => _gnomes.ContainsKey(connId);

        private static float ResolveCurveValue(int level, int[] levels, float[] values, float fallback)
        {
            if (levels == null || values == null || levels.Length == 0 || levels.Length != values.Length)
                return fallback;

            level = Mathf.Max(1, level);
            if (level <= levels[0])
                return values[0];

            for (int i = 1; i < levels.Length; i++)
            {
                if (level <= levels[i])
                {
                    int prevLevel = levels[i - 1];
                    int nextLevel = levels[i];
                    float prevValue = values[i - 1];
                    float nextValue = values[i];
                    if (nextLevel <= prevLevel)
                        return nextValue;
                    float t = (float)(level - prevLevel) / (nextLevel - prevLevel);
                    return prevValue + (nextValue - prevValue) * t;
                }
            }

            return values[values.Length - 1];
        }

        private static uint ResolveBlingGnomeHitPointsWire(int level)
        {
            int curveRaw = ToFixedRounded(ResolveCurveValue(level, HENCHMAN_HEALTH_LEVELS, HENCHMAN_HEALTH_VALUES, 245f));
            int scaleRaw = ToFixedRounded(BLING_HEALTH_SCALE);
            long health = ((long)curveRaw * scaleRaw) >> 16;
            if (health < 1)
                health = 1;
            return (uint)health * 256u;
        }

        public uint GetGnomeEntityId(int connId)
        {
            if (_gnomes.TryGetValue(connId, out var g)) return g.EntityId;
            return 0;
        }

        public void CleanupForZoneTransition(int connId)
        {
            if (_gnomes.TryGetValue(connId, out var g))
            {
                Debug.LogError($"[GNOME] Cleaning up gnome (ID=0x{g.EntityId:X4}) for zone transition");
                if (g.AICoroutine != null) StopCoroutine(g.AICoroutine);
                if (g.ConversionCoroutine != null) StopCoroutine(g.ConversionCoroutine);
                g.BehaviorBootstrapped = false;
                _gnomes.Remove(connId);
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // CHAT COMMAND HANDLING
        // ═══════════════════════════════════════════════════════════════════

        public void HandleChatMessage(RRConnection conn, byte[] data, SendPacketDelegate sendPacket, SendMessageDelegate sendMessage)
        {
            try
            {
                if (data == null || data.Length < 1) return;
                var reader = new LEReader(data);
                string message = reader.ReadCString();
                if (message.StartsWith("@"))
                {
                    string command = message.Substring(1).ToLower().Trim();
                    if (command == "gnome" || command == "bling" || command == "blinggnome")
                        ForceToggleGnome(conn, sendPacket, sendMessage);
                    else if (command == "gnomestatus" || command == "gs")
                        ShowGnomeStatus(conn, sendMessage);
                }
            }
            catch (Exception ex) { Debug.LogError($"[GNOME-CHAT] Error: {ex.Message}"); }
        }

        private void ShowGnomeStatus(RRConnection conn, SendMessageDelegate sendMessage)
        {
            if (!_gnomes.TryGetValue(conn.ConnId, out var g))
            {
                sendMessage(conn, "[Bling Gnome] No gnome active. Use skill or @gnome to summon!");
                return;
            }
            string activeStr = g.IsActive ? $" | ACTIVE ({Mathf.Max(0f, g.ActivateUntil - Time.time):F0}s)" : "";
            sendMessage(conn, $"[Bling Gnome] Items: {g.ItemsConverted} | Gold: {g.GoldGenerated} | Persistent{activeStr}");
        }

        private void ResolveOwnerAnchor(RRConnection conn, out float posX, out float posY, out float posZ, out float heading)
        {
            if (conn != null && conn.HasLivePlayerPosition)
            {
                posX = conn.LivePlayerPosX;
                posY = conn.LivePlayerPosY;
                posZ = conn.LivePlayerPosZ;
                heading = conn.LivePlayerHeading;
                return;
            }

            posX = conn != null ? conn.PlayerPosX : 0f;
            posY = conn != null ? conn.PlayerPosY : 0f;
            posZ = conn != null ? conn.PlayerPosZ : 0f;
            heading = conn != null ? conn.PlayerHeading : 0f;
        }

        // ═══════════════════════════════════════════════════════════════════
        // TOGGLE / SPAWN / DESPAWN
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Called from HandleSelfCastSpell when player casts SummonBlingGnome from hotbar.
        /// First cast: spawn gnome. Subsequent casts: activate 10s conversion window.
        /// </summary>
        public void ToggleGnome(RRConnection conn, SendPacketDelegate sendPacket, SendMessageDelegate sendMessage)
        {
            if (_gnomes.ContainsKey(conn.ConnId))
            {
                // Gnome exists — ACTIVATE conversion (don't despawn!)
                ActivateGnome(conn);
            }
            else
            {
                SpawnGnome(conn, sendPacket, sendMessage);
            }
        }

        /// <summary>
        /// Force despawn — used by @gnome chat command only.
        /// </summary>
        public void ForceToggleGnome(RRConnection conn, SendPacketDelegate sendPacket, SendMessageDelegate sendMessage)
        {
            if (_gnomes.ContainsKey(conn.ConnId))
            {
                var g = _gnomes[conn.ConnId];
                if (g.GoldGenerated > 0)
                    sendMessage(conn, $"[Bling Gnome] Converted {g.ItemsConverted} items into {g.GoldGenerated} gold!");
                DespawnGnome(conn, sendPacket);
            }
            else
            {
                SpawnGnome(conn, sendPacket, sendMessage);
            }
        }

        /// <summary>
        /// Activate the BlingGnome's conversion ability (called when player casts SummonBlingGnome skill on gnome).
        /// Opens a 10-second window where items within radius are converted to gold.
        /// </summary>
        public void ActivateGnome(RRConnection conn)
        {
            if (!_gnomes.TryGetValue(conn.ConnId, out var g)) return;
            if (!g.BehaviorBootstrapped) return;

            g.IsActive = true;
            g.ActivateUntil = Time.time + EFFECT_DURATION_SECONDS;
            g.ItemsConverted = 0;
            g.GoldGenerated = 0;
            g.NextSearchTime = Time.time; // search immediately

            // Send ConvertItemsToGold action (0xA1) — triggers client's native conversion visuals
            SendConvertItemsToGoldAction(conn, g);
            if (g.ConversionCoroutine != null) StopCoroutine(g.ConversionCoroutine);
            g.ConversionCoroutine = StartCoroutine(ApplyActiveConversion(conn, g));

            Debug.LogError($"[GNOME-ACTIVATE] Conversion window open for {EFFECT_DURATION_SECONDS}s, radius={CONVERT_SEARCH_RADIUS}");
        }

        // ═══════════════════════════════════════════════════════════════════
        // SPAWN — Entity snapshot + delayed behavior bootstrap
        // ═══════════════════════════════════════════════════════════════════

        public void SpawnGnome(RRConnection conn, SendPacketDelegate sendPacket, SendMessageDelegate sendMessage)
        {
            Debug.LogError($"[GNOME-SPAWN] Spawning BlingGnome_Summon for {conn.LoginName}");

            // Allocate entity ID for the gnome
            ushort gnomeId = (ushort)_nextEntityId++;

            // Component IDs MUST match Go's worldNPCComponentIDFromEntityID formula.
            // Client's BlingGnomeBehavior uses this exact formula to find sibling
            // components. If IDs don't match, lookups return NULL → crash at 0x535A1E.
            // From missing_codes.txt:
            //   id = (entityID & 0xFF00) + 0x0100 + (entityID & 0x00FF) * 0x10 + slot
            //   Slots: Modifiers=5, Manipulators=6, UnitBehavior=7
            ushort modifiersId = ComputeNPCComponentId(gnomeId, 0x05);
            ushort manipulatorsId = ComputeNPCComponentId(gnomeId, 0x06);
            ushort behaviorId = ComputeNPCComponentId(gnomeId, 0x07);
            Debug.LogError($"[GNOME-SPAWN] entity=0x{gnomeId:X4} mod=0x{modifiersId:X4} manip=0x{manipulatorsId:X4} beh=0x{behaviorId:X4}");

            // Position near player
            string zoneName = conn.CurrentZoneName ?? "Town";
            ResolveOwnerAnchor(conn, out float ownerX, out float ownerY, out float ownerZ, out float ownerHeading);
            float posXf = ownerX + 5f;
            float posYf = ownerY + 5f;
            float posZf = PathMapManager.Instance.GetHeight(zoneName, posXf, posYf, ownerZ);
            float headingf = ownerHeading;

            var g = new GnomeState
            {
                EntityId = gnomeId,
                BehaviorId = behaviorId,
                ModifiersId = modifiersId,
                ManipulatorsId = manipulatorsId,
                PosX = posXf,
                PosY = posYf,
                PosZ = posZf,
                Heading = headingf,
                SpawnPosX = posXf,
                SpawnPosY = posYf,
                SpawnHeading = headingf,
                SpawnTime = Time.time,
                BehaviorBootstrapped = false,
                IsActive = false,
                NextFidgetTime = Time.time + GetFidgetDelay(),
                NextSearchTime = Time.time + SEARCH_PULSE_SECONDS,
                SendPacket = sendPacket,
                SendMessage = sendMessage
            };
            _gnomes[conn.ConnId] = g;

            // ══════════════════════════════════════════════════════
            // PHASE 1: Entity Snapshot (creates the 3D model)
            // Op 0x01 (Create Entity) + Op 0x02 (Init Entity)
            // NO behavior component yet — added in delayed bootstrap
            // ══════════════════════════════════════════════════════
            SendEntitySnapshot(conn, g);

            if (COMPONENT_TEST_MODE > 0)
            {
                StartCoroutine(DelayedBehaviorBootstrap(conn, g));
            }
            else
            {
                Debug.LogError("[GNOME] COMPONENT_TEST_MODE=0, skipping bootstrap");
            }

            // Start AI loop (follow, fidget, etc.)
            if (COMPONENT_TEST_MODE >= 3)
            {
                g.AICoroutine = StartCoroutine(GnomeAILoop(conn));
            }
        }

        /// <summary>
        /// Send entity snapshot — creates the gnome model in the world.
        /// Op 0x01 (CreateEntity) + Op 0x02 (InitEntity)
        /// No components yet — those come in the delayed bootstrap.
        /// </summary>
        private void SendEntitySnapshot(RRConnection conn, GnomeState g)
        {
            int posX_fx = ToFixed(g.PosX);
            int posY_fx = ToFixed(g.PosY);
            int posZ_fx = ToFixed(g.PosZ);
            int heading_fx = ToFixed(g.Heading);

            ushort ownerEntityId = 0;
            if (conn.Player != null && conn.Player.Id > 0)
                ownerEntityId = (ushort)conn.Player.Id;
            if (ownerEntityId == 0 && _server != null)
                ownerEntityId = (ushort)_server.GetPlayerAvatarId(conn.LoginName);

            int level = conn.PlayerLevel;
            if (_server != null)
            {
                PlayerState playerState = _server.GetPlayerState(conn.ConnId.ToString());
                if (playerState != null && playerState.Level > 0)
                    level = playerState.Level;
            }
            level = Mathf.Clamp(level, 1, 255);
            byte unitFlags = 0x16;
            if (ownerEntityId != 0)
                unitFlags = (byte)(unitFlags | 0x01);
            uint hitPointsWire = ResolveBlingGnomeHitPointsWire(level);
            g.HitPointsWire = hitPointsWire;
            const uint manaPointsWire = 0;

            var writer = new LEWriter();
            writer.WriteByte(0x07); // ClientEntityChannel

            // ── Op 0x01: Create Entity ──
            writer.WriteByte(0x01);
            writer.WriteUInt16(g.EntityId);
            writer.WriteByte(0xFF);
            writer.WriteCString(ENTITY_GC_TYPE);

            if (ENTITY_INIT_MODE >= 1)
            {
                // ── Op 0x02: Init Entity ──
                writer.WriteByte(0x02);
                writer.WriteUInt16(g.EntityId);

                // Entity::readInit (21 bytes)
                writer.WriteUInt32(0x06);
                writer.WriteInt32(posX_fx);
                writer.WriteInt32(posY_fx);
                writer.WriteInt32(posZ_fx);
                writer.WriteInt32(heading_fx);
                writer.WriteByte(0x00);

                writer.WriteByte(unitFlags);
                writer.WriteByte((byte)level);
                writer.WriteUInt16(0);
                writer.WriteUInt16(0);
                if ((unitFlags & 0x01) != 0)
                    writer.WriteUInt16(ownerEntityId);
                writer.WriteUInt32(hitPointsWire);
                writer.WriteUInt32(manaPointsWire);
                writer.WriteByte(0x00);

                writer.WriteByte(0x00);
                writer.WriteUInt16(0); writer.WriteUInt16(0);
                writer.WriteByte(0x00);
                writer.WriteUInt16(0); writer.WriteUInt32(0);
                writer.WriteByte(0x00);
                writer.WriteUInt32(0); writer.WriteUInt32(0); writer.WriteUInt32(0);
                Debug.LogError($"[GNOME-SPAWN] Native StockUnit init: unitFlags=0x{unitFlags:X2}, level={level}, owner=0x{ownerEntityId:X4}, hp=0x{hitPointsWire:X8}, mana=0x{manaPointsWire:X8}");
            }
            else
            {
                Debug.LogError($"[GNOME-SPAWN] Mode 0: CREATE ONLY - no Op 0x02 init");
            }

            writer.WriteByte(0x06); // EndStream

            byte[] pkt = writer.ToArray();
            Debug.LogError($"[GNOME-SPAWN] Entity snapshot: {pkt.Length} bytes, mode={ENTITY_INIT_MODE}, hex={BitConverter.ToString(pkt)}");
            g.SendPacket(conn, 0x01, 0x0F, pkt);
        }

        /// <summary>
        /// Delayed behavior bootstrap — creates component hierarchy after entity exists.
        /// Go server delays ~66ms.
        /// Creates: Modifiers → Manipulators → BlingGnomeBehavior (with SM2 init)
        /// Then sends spawn poof animation (PlayAnimation 180).
        /// </summary>
        private IEnumerator DelayedBehaviorBootstrap(RRConnection conn, GnomeState g)
        {
            yield return new WaitForSeconds(BEHAVIOR_BOOTSTRAP_DELAY);

            if (!_gnomes.TryGetValue(conn.ConnId, out var current) || !object.ReferenceEquals(current, g) || !conn.IsConnected)
                yield break;

            int posX_fx = ToFixed(g.PosX);
            int posY_fx = ToFixed(g.PosY);

            // ══════════════════════════════════════════════════════════════
            // Send each component as a SEPARATE stream message, matching
            // Go server's buildCH07ComponentCreateSubmsg pattern.
            // Each gets its own BeginStream/EndStream envelope.
            // ══════════════════════════════════════════════════════════════

            // ══════════════════════════════════════════════════════════════
            // Send all 3 components in ONE stream — matching Go pattern:
            // worldQueueRuntimeSubmsgs(st, msgs) where msgs has all 3
            // ══════════════════════════════════════════════════════════════

            if (COMPONENT_TEST_MODE >= 1)
            {
                var w = new LEWriter();
                w.WriteByte(0x07); // BeginStream

                // ── 1. Modifiers — EXACT Go format (9 bytes payload) ──
                // buildWorldCreatureModifiersComponentInitPayload (no active modifiers):
                //   u32(0) | u32(creature.ModifierServerCounter) | byte(0) [empty children]
                w.WriteByte(0x32);
                w.WriteUInt16(g.EntityId);
                w.WriteUInt16(g.ModifiersId);
                w.WriteByte(0xFF);
                w.WriteCString("Modifiers");
                w.WriteByte(0x01);          // active flag (CH07ComponentCreateSubmsg)
                w.WriteUInt32(0);           // first u32 = 0
                w.WriteUInt32(0);           // ModifierServerCounter = 0
                w.WriteByte(0x00);          // empty children list (buildWorldWriteChildDataItemsEmpty)

                // ── 2. Manipulators — EXACT Go format (1 byte payload) ──
                // For native BlingGnomeBehavior: payload = buildWorldWriteChildDataItemsEmpty() = [0x00]
                if (COMPONENT_TEST_MODE >= 2)
                {
                    w.WriteByte(0x32);
                    w.WriteUInt16(g.EntityId);
                    w.WriteUInt16(g.ManipulatorsId);
                    w.WriteByte(0xFF);
                    w.WriteCString("Manipulators");
                    w.WriteByte(0x01);      // active flag (CH07ComponentCreateSubmsg)
                    w.WriteByte(0x00);      // empty children list
                }

                // ── 3. Behavior — EXACT Go reference format (28 bytes payload) ──
                // From buildWorldBlingGnomeBehaviorComponentInitPayload in missing_codes.txt:
                //   moverPayload = buildWorldUnitBehaviorComponentInitPayloadWithMode(x, y, 0x00, Stopped=0x01)
                //   smPayload    = buildWorldBlingGnomeStateMachineInitPayload (flags=0x09, RequestedState=0x0000)
                //   anchor       = int32(x*256), int32(y*256)
                if (COMPONENT_TEST_MODE >= 3)
                {
                    int anchorX_fx = ToFixed(g.PosX);  // worldCoordToFixed(x) = x * 256
                    int anchorY_fx = ToFixed(g.PosY);

                    w.WriteByte(0x32);
                    w.WriteUInt16(g.EntityId);
                    w.WriteUInt16(g.BehaviorId);
                    w.WriteByte(0xFF);
                    w.WriteCString(BEHAVIOR_GC_TYPE);  // "creatures.summon.blinggnome.base.BlingGnome_Summon.Behavior"
                    w.WriteByte(0x01);                  // active flag (CH07ComponentCreateSubmsg)

                    // ── moverPayload (17 bytes) — buildWorldUnitBehaviorComponentInitPayloadWithMode ──
                    w.WriteByte(0x00);                  // Behavior::readInit byte 0
                    w.WriteByte(0x00);                  // Behavior::readInit byte 1
                    w.WriteByte(0x00);                  // Behavior::readInit byte 2
                    w.WriteByte(0x01);                  // Behavior::readInit byte 3
                    w.WriteByte(0x42);                  // UnitMover init flags
                    w.WriteInt32(anchorX_fx);           // posX (Fixed)
                    w.WriteInt32(anchorY_fx);           // posY (Fixed)
                    w.WriteByte(0x01);                  // moverMode = Stopped (UnitMover::StopMoving stores 0x01)
                    w.WriteByte(0x00);                  // lead = 0
                    w.WriteByte(0x00);                  // UnitBehavior unk1
                    w.WriteByte(0x00);                  // UnitBehavior unk2

                    // ── smPayload (3 bytes) — buildWorldBlingGnomeStateMachineInitPayload ──
                    // SM init flags = TransitionPending(0x01) | RequestedState(0x08) = 0x09
                    // SM2::readInit @ 0x5F0C70: bit 3 (0x08) triggers uint16 RequestedState read
                    w.WriteByte(0x09);                  // SM flags
                    w.WriteUInt16(0x0000);              // RequestedState = StateInit (0x0000)

                    // ── anchor (8 bytes) — written directly by 0x516970 ──
                    w.WriteInt32(anchorX_fx);           // anchorX → behavior+0x1A4
                    w.WriteInt32(anchorY_fx);           // anchorY → behavior+0x1A8
                    WritePlayAnimationSubmsg(w, g, ANIM_SPAWN, ANIM_STATE_DIRECT, 24);
                }

                w.WriteByte(0x06); // EndStream

                byte[] pkt = w.ToArray();
                Debug.LogError($"[GNOME-BOOTSTRAP] All-in-one packet ({pkt.Length} bytes), mode={COMPONENT_TEST_MODE}");
                g.SendPacket(conn, 0x01, 0x0F, pkt);
            }

            if (COMPONENT_TEST_MODE >= 3)
            {
                g.BehaviorBootstrapped = true;
                Debug.LogError($"[GNOME-BOOTSTRAP] All 3 components created: mod=0x{g.ModifiersId:X4} manip=0x{g.ManipulatorsId:X4} beh=0x{g.BehaviorId:X4}");

                Debug.LogError("[GNOME-BOOTSTRAP] Spawn poof animation sent inline (anim 180)");
            }
            else
            {
                Debug.LogError($"[GNOME-BOOTSTRAP] COMPONENT_TEST_MODE={COMPONENT_TEST_MODE}, skipping behavior/poof");
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // DESPAWN — death animation + entity remove
        // ═══════════════════════════════════════════════════════════════════

        public void DespawnGnome(RRConnection conn, SendPacketDelegate sendPacket)
        {
            if (!_gnomes.TryGetValue(conn.ConnId, out var g)) return;
            if (g.AICoroutine != null) StopCoroutine(g.AICoroutine);
            if (g.ConversionCoroutine != null) StopCoroutine(g.ConversionCoroutine);

            // Send death/depoof animation if behavior is bootstrapped
            if (g.BehaviorBootstrapped && conn.IsConnected)
            {
                SendPlayAnimation(conn, g, ANIM_DEATH, ANIM_STATE_DIRECT, 24);

                // Delayed entity remove (let death animation play)
                StartCoroutine(DelayedEntityRemove(conn, g, sendPacket, 0.8f));
            }
            else
            {
                // No behavior — just remove immediately
                SendEntityRemove(conn, g, sendPacket);
                _gnomes.Remove(conn.ConnId);
            }
        }

        private IEnumerator DelayedEntityRemove(RRConnection conn, GnomeState g, SendPacketDelegate sendPacket, float delay)
        {
            yield return new WaitForSeconds(delay);
            if (conn != null && conn.IsConnected)
                SendEntityRemove(conn, g, sendPacket);
            if (g.ConversionCoroutine != null) StopCoroutine(g.ConversionCoroutine);
            _gnomes.Remove(conn.ConnId);
        }

        private void SendEntityRemove(RRConnection conn, GnomeState g, SendPacketDelegate sendPacket)
        {
            var writer = new LEWriter();
            writer.WriteByte(0x07);
            writer.WriteByte(0x05); // RemoveEntity
            writer.WriteUInt16(g.EntityId);
            writer.WriteByte(0x06);
            sendPacket(conn, 0x01, 0x0F, writer.ToArray());
            Debug.LogError($"[GNOME] Despawned 0x{g.EntityId:X4} — {g.ItemsConverted} items → {g.GoldGenerated}g");
        }

        // ═══════════════════════════════════════════════════════════════════
        // AI LOOP — Server-driven behavior
        // ═══════════════════════════════════════════════════════════════════

        private IEnumerator GnomeAILoop(RRConnection conn)
        {
            yield return new WaitForSeconds(BEHAVIOR_BOOTSTRAP_DELAY + 0.2f);

            while (conn != null && conn.IsConnected && _gnomes.TryGetValue(conn.ConnId, out var g))
            {
                float now = Time.time;

                if (g.IsActive && now >= g.ActivateUntil)
                {
                    g.IsActive = false;
                    Debug.LogError("[GNOME-AI] Activation window expired");
                }

                UpdateFollowMirror(conn, g, now);

                if (now >= g.NextSearchTime)
                {
                    g.NextSearchTime = now + SEARCH_PULSE_SECONDS;
                    if (TryFindNearestGoldDrop(conn, g, out var closestId, out var closestInfo))
                    {
                        yield return StartCoroutine(PickupGroundItem(conn, g, closestId, closestInfo));
                        yield return new WaitForSeconds(AI_TICK_INTERVAL);
                        continue;
                    }
                }

                if (g.BehaviorBootstrapped && now >= g.NextFidgetTime && !g.IsActive && !g.HasMoveTarget)
                {
                    ushort fidgetAnim = PickFidgetAnimation(g);
                    if (fidgetAnim != 0)
                    {
                        SendPlayAnimation(conn, g, fidgetAnim, ANIM_STATE_FIDGET, 40);
                        g.NextFidgetTime = now + GetFidgetDelay();
                    }
                }

                yield return new WaitForSeconds(AI_TICK_INTERVAL);
            }
        }

        // ═══════════════════════════════════════════════════════════════════
        // PICKUP & CONVERSION
        // ═══════════════════════════════════════════════════════════════════

        private IEnumerator PickupGroundItem(RRConnection conn, GnomeState g, ushort entityId, UnityGameServer.DroppedItemInfo item)
        {
            bool goldDrop = IsGoldLike(item);
            if (g.BehaviorBootstrapped && !goldDrop)
                SendPlayAnimation(conn, g, ANIM_PICKUP, ANIM_STATE_DIRECT, 16);

            yield return new WaitForSeconds(goldDrop ? 0.1f : 0.3f);

            if (!_gnomes.TryGetValue(conn.ConnId, out var current) || current.EntityId != g.EntityId || !conn.IsConnected)
                yield break;

            uint goldAmount;
            if (goldDrop)
            {
                goldAmount = item.GoldAmount;
            }
            else if (current.IsActive && CanConvertItem(conn, item))
            {
                goldAmount = CalculateConversionGold(conn, item);
            }
            else
            {
                yield break;
            }

            if (goldAmount == 0) goldAmount = 1;

            _server.BlingGnomePickupItem(conn, entityId);
            _server.BlingGnomeCreditGold(conn, goldAmount);

            current.ItemsConverted++;
            current.GoldGenerated += goldAmount;

            Debug.LogError($"[GNOME-PICKUP] Item 0x{entityId:X4} → {goldAmount}g (total: {current.GoldGenerated}g)");

            yield return new WaitForSeconds(0.2f);
        }

        private IEnumerator ApplyActiveConversion(RRConnection conn, GnomeState g)
        {
            yield return new WaitForSeconds(0.15f);

            if (!_gnomes.TryGetValue(conn.ConnId, out var current) || current.EntityId != g.EntityId || !conn.IsConnected)
                yield break;

            string zone = conn.CurrentZoneName ?? "";
            uint instId = conn.InstanceId;
            ResolveOwnerAnchor(conn, out float ownerX, out float ownerY, out _, out _);
            var candidates = new List<(ushort entityId, UnityGameServer.DroppedItemInfo info)>();
            var seen = new HashSet<ushort>();

            AddConversionCandidates(_server.GetDroppedItemsNear(zone, instId, current.PosX, current.PosY, CONVERT_SEARCH_RADIUS), conn, candidates, seen);
            AddConversionCandidates(_server.GetDroppedItemsNear(zone, instId, ownerX, ownerY, CONVERT_SEARCH_RADIUS), conn, candidates, seen);
            candidates.Sort((a, b) => a.entityId.CompareTo(b.entityId));

            foreach (var candidate in candidates)
            {
                if (!_gnomes.TryGetValue(conn.ConnId, out current) || current.EntityId != g.EntityId || !current.IsActive || !conn.IsConnected)
                    yield break;
                if (!CanConvertItem(conn, candidate.info))
                    continue;

                uint goldAmount = CalculateConversionGold(conn, candidate.info);
                if (goldAmount == 0) goldAmount = 1;

                _server.BlingGnomePickupItem(conn, candidate.entityId);
                _server.BlingGnomeCreditGold(conn, goldAmount);

                current.ItemsConverted++;
                current.GoldGenerated += goldAmount;

                Debug.LogError($"[GNOME-CONVERT] Item 0x{candidate.entityId:X4} → {goldAmount}g (total: {current.GoldGenerated}g)");
                yield return new WaitForSeconds(0.05f);
            }
        }

        private void AddConversionCandidates(List<(ushort entityId, UnityGameServer.DroppedItemInfo info)> items, RRConnection conn, List<(ushort entityId, UnityGameServer.DroppedItemInfo info)> candidates, HashSet<ushort> seen)
        {
            if (items == null) return;
            foreach (var item in items)
            {
                if (seen.Contains(item.entityId)) continue;
                if (!CanConvertItem(conn, item.info)) continue;
                seen.Add(item.entityId);
                candidates.Add(item);
            }
        }

        private bool TryFindNearestGoldDrop(RRConnection conn, GnomeState g, out ushort entityId, out UnityGameServer.DroppedItemInfo item)
        {
            entityId = 0;
            item = null;
            float closestDistSq = float.MaxValue;
            string zone = conn.CurrentZoneName ?? "";
            uint instId = conn.InstanceId;
            ResolveOwnerAnchor(conn, out float ownerX, out float ownerY, out _, out _);

            ConsiderGoldCandidates(_server.GetDroppedItemsNear(zone, instId, g.PosX, g.PosY, BEHAVIOR_SEARCH_RANGE), conn, g, ownerX, ownerY, ref entityId, ref item, ref closestDistSq);
            ConsiderGoldCandidates(_server.GetDroppedItemsNear(zone, instId, ownerX, ownerY, BEHAVIOR_SEARCH_RANGE), conn, g, ownerX, ownerY, ref entityId, ref item, ref closestDistSq);
            return item != null;
        }

        private void ConsiderGoldCandidates(List<(ushort entityId, UnityGameServer.DroppedItemInfo info)> items, RRConnection conn, GnomeState g, float ownerX, float ownerY, ref ushort entityId, ref UnityGameServer.DroppedItemInfo closestItem, ref float closestDistSq)
        {
            if (items == null) return;
            foreach (var candidate in items)
            {
                var info = candidate.info;
                if (!IsGoldLike(info) || !IsAllowedForOwner(conn, info)) continue;
                float distSq = Mathf.Min(
                    DistSq(info.PosX, info.PosY, g.PosX, g.PosY),
                    DistSq(info.PosX, info.PosY, ownerX, ownerY));
                if (distSq >= closestDistSq) continue;
                closestDistSq = distSq;
                entityId = candidate.entityId;
                closestItem = info;
            }
        }

        private bool IsAllowedForOwner(RRConnection conn, UnityGameServer.DroppedItemInfo item)
        {
            if (item == null) return false;
            return string.IsNullOrWhiteSpace(item.DroppedBy) || string.Equals(item.DroppedBy, conn.LoginName ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsGoldLike(UnityGameServer.DroppedItemInfo item)
        {
            return item != null && (item.IsGoldDrop || item.GoldAmount > 0);
        }

        private bool CanConvertItem(RRConnection conn, UnityGameServer.DroppedItemInfo item)
        {
            if (item == null || item.Item == null) return false;
            if (item.IsQuestItem || IsGoldLike(item) || !IsAllowedForOwner(conn, item)) return false;
            if (item.Item.GetEffectiveRarity() > 3) return false;
            string gc = item.Item.GCClass ?? "";
            return gc.IndexOf("quest", StringComparison.OrdinalIgnoreCase) < 0
                && gc.IndexOf("unique", StringComparison.OrdinalIgnoreCase) < 0
                && gc.IndexOf("collection", StringComparison.OrdinalIgnoreCase) < 0
                && gc.IndexOf("mythic", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private uint CalculateConversionGold(RRConnection conn, UnityGameServer.DroppedItemInfo item)
        {
            uint baseGold = _server.GetItemGoldValue(item);
            if (baseGold == 0) baseGold = (uint)Math.Max(1, item.PlayerLevel * 2);

            float converted = baseGold * CONVERSION_RATIO * GOLD_VALUE_MOD;
            return (uint)Math.Max(1, (int)converted);
        }

        // ═══════════════════════════════════════════════════════════════════
        // PACKET BUILDERS — Movement & Animation
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Send mover update via 0x65 sub-op on the behavior component.
        /// This updates the gnome's position on the minimap and in the world.
        /// Format matches working monster mover updates from CombatPackets.
        /// </summary>
        private void SendMoverUpdate(RRConnection conn, GnomeState g, bool terminal, float now)
        {
            if (!g.BehaviorBootstrapped) return;
            if (!SendServerGnomeMoverUpdates) return;

            float prevHeading = g.LastMoverSyncValid ? g.LastMoverSyncHeading : g.SpawnHeading;
            float prevX = g.LastMoverSyncValid ? g.LastMoverSyncPosX : g.SpawnPosX;
            float prevY = g.LastMoverSyncValid ? g.LastMoverSyncPosY : g.SpawnPosY;
            byte flags = BuildBlingMoverFlags(prevHeading, prevX, prevY, g.Heading, g.PosX, g.PosY, terminal);

            var writer = new LEWriter();
            writer.WriteByte(0x07); // BeginStream

            writer.WriteByte(0x35);               // ComponentUpdate
            writer.WriteUInt16(g.BehaviorId);
            writer.WriteByte(0x65);               // mover update sub-op
            writer.WriteByte(0x00);               // lead byte

            if (terminal)
            {
                writer.WriteByte(0x02);
                WriteMoverRecord(writer, (byte)((BuildBlingMoverFlags(g.Heading, g.PosX, g.PosY, g.Heading, g.PosX + 1f, g.PosY, false) | 0x06) & 0x06), g.Heading, g.PosX + 1f, g.PosY);
                WriteMoverRecord(writer, (byte)((flags | 0x03) & 0x07), g.Heading, g.PosX, g.PosY);
            }
            else
            {
                writer.WriteByte(0x01);
                WriteMoverRecord(writer, flags, g.Heading, g.PosX, g.PosY);
            }

            WriteGnomeEntitySynchInfo(writer, g);
            writer.WriteByte(0x06); // EndStream

            g.SendPacket(conn, 0x01, 0x0F, writer.ToArray());
            g.LastMoverSyncValid = true;
            g.LastMoverSyncAt = now;
            g.LastMoverSyncPosX = g.PosX;
            g.LastMoverSyncPosY = g.PosY;
            g.LastMoverSyncHeading = g.Heading;
            g.LastMoverSyncTerminal = terminal;
        }

        private void UpdateFollowMirror(RRConnection conn, GnomeState g, float now)
        {
            if (!g.BehaviorBootstrapped) return;

            ResolveOwnerAnchor(conn, out float ownerX, out float ownerY, out float ownerZ, out float ownerHeading);
            float followX = ownerX;
            float followY = ownerY;
            int offsetIndex = g.EntityId % 4;
            float offsetX = (offsetIndex == 1 || offsetIndex == 3) ? -18f : 18f;
            float offsetY = offsetIndex >= 2 ? -10f : 10f;
            if (DistSq(g.PosX, g.PosY, ownerX, ownerY) < FOLLOW_TOO_CLOSE * FOLLOW_TOO_CLOSE)
            {
                followX += offsetX;
                followY += offsetY;
            }

            float followZ = ownerZ;
            float followHeading = ownerHeading;
            float ownerDistSq = DistSq(g.PosX, g.PosY, ownerX, ownerY);
            float followDistSq = DistSq(g.PosX, g.PosY, followX, followY);
            bool followMoveActive = g.HasMoveTarget;
            bool followShouldPath = ShouldBlingFollowPath(followMoveActive, ownerDistSq, followDistSq);
            bool changed = false;
            bool stateChanged = false;

            if (followShouldPath)
            {
                if (g.HasMoveTarget && DistSq(g.MoveTargetX, g.MoveTargetY, followX, followY) < FOLLOW_RETARGET_DEADBAND * FOLLOW_RETARGET_DEADBAND)
                {
                    followX = g.MoveTargetX;
                    followY = g.MoveTargetY;
                }

                if (!g.HasMoveTarget || !NearlyEqual(g.MoveTargetX, followX) || !NearlyEqual(g.MoveTargetY, followY))
                {
                    g.MoveTargetX = followX;
                    g.MoveTargetY = followY;
                    g.HasMoveTarget = true;
                    stateChanged = true;
                }

                bool moveDue = g.LastFollowRuntimeAt <= 0f || now - g.LastFollowRuntimeAt >= FOLLOW_STEP_MIN_INTERVAL;
                if (moveDue)
                {
                    float dx = followX - g.PosX;
                    float dy = followY - g.PosY;
                    if (!NearlyEqual(dx, 0f) || !NearlyEqual(dy, 0f))
                    {
                        float step = ComputeFollowStep(g, now);
                        ProjectStep(g.PosX, g.PosY, followX, followY, step, out float newX, out float newY);
                        float heading = HeadingFromDelta(dx, dy, g.Heading);
                        if (!NearlyEqual(g.PosX, newX) || !NearlyEqual(g.PosY, newY))
                        {
                            g.PosX = newX;
                            g.PosY = newY;
                            changed = true;
                        }
                        if (!NearlyEqual(g.Heading, heading))
                        {
                            g.Heading = heading;
                            changed = true;
                        }
                    }
                    g.LastFollowRuntimeAt = now;
                }

                if (!NearlyEqual(g.PosZ, followZ))
                {
                    g.PosZ = followZ;
                    changed = true;
                }

                if (NearlyEqual(g.PosX, followX) && NearlyEqual(g.PosY, followY))
                {
                    if (g.HasMoveTarget || !NearlyEqual(g.MoveTargetX, 0f) || !NearlyEqual(g.MoveTargetY, 0f))
                    {
                        g.HasMoveTarget = false;
                        g.MoveTargetX = 0f;
                        g.MoveTargetY = 0f;
                        stateChanged = true;
                    }
                    if (!NearlyEqual(g.Heading, followHeading))
                    {
                        g.Heading = followHeading;
                        changed = true;
                    }
                }
            }
            else if (followMoveActive)
            {
                if (g.HasMoveTarget || !NearlyEqual(g.MoveTargetX, 0f) || !NearlyEqual(g.MoveTargetY, 0f))
                {
                    g.HasMoveTarget = false;
                    g.MoveTargetX = 0f;
                    g.MoveTargetY = 0f;
                    stateChanged = true;
                }
                if (!NearlyEqual(g.PosZ, followZ))
                {
                    g.PosZ = followZ;
                    changed = true;
                }
                if (!NearlyEqual(g.Heading, followHeading))
                {
                    g.Heading = followHeading;
                    changed = true;
                }
            }

            bool terminalResyncNeeded = !followShouldPath && !g.HasMoveTarget && g.LastMoverSyncValid && !g.LastMoverSyncTerminal;
            if ((changed || stateChanged || terminalResyncNeeded) && MoverSyncDue(g, now))
            {
                SendMoverUpdate(conn, g, !g.HasMoveTarget, now);
            }
        }

        private bool ShouldBlingFollowPath(bool followMoveActive, float ownerDistSq, float followDistSq)
        {
            if (ownerDistSq < FOLLOW_TOO_CLOSE * FOLLOW_TOO_CLOSE && followDistSq > FOLLOW_RETARGET_DEADBAND * FOLLOW_RETARGET_DEADBAND)
                return true;
            if (followDistSq <= FOLLOW_SETTLE_RADIUS * FOLLOW_SETTLE_RADIUS)
                return false;
            if (followMoveActive)
                return true;
            return ownerDistSq > FOLLOW_START_RADIUS * FOLLOW_START_RADIUS;
        }

        private bool MoverSyncDue(GnomeState g, float now)
        {
            float prevX = g.LastMoverSyncValid ? g.LastMoverSyncPosX : g.SpawnPosX;
            float prevY = g.LastMoverSyncValid ? g.LastMoverSyncPosY : g.SpawnPosY;
            float prevHeading = g.LastMoverSyncValid ? g.LastMoverSyncHeading : g.SpawnHeading;
            bool terminal = !g.HasMoveTarget;
            float movedSq = DistSq(prevX, prevY, g.PosX, g.PosY);
            bool headingChanged = !NearlyEqual(prevHeading, g.Heading);
            bool terminalChanged = g.LastMoverSyncValid && g.LastMoverSyncTerminal != terminal;

            if (movedSq <= 0f && !headingChanged && !terminalChanged)
                return false;
            if (!g.LastMoverSyncValid || g.LastMoverSyncAt <= 0f)
                return true;
            if (terminalChanged)
                return true;
            if (g.HasMoveTarget)
            {
                if (movedSq >= MOVER_SYNC_ACTIVE_MAX_DRIFT * MOVER_SYNC_ACTIVE_MAX_DRIFT)
                    return true;
                return now - g.LastMoverSyncAt >= MOVER_SYNC_ACTIVE_MIN_INTERVAL;
            }
            if (movedSq >= MOVER_SYNC_SETTLED_MAX_DRIFT * MOVER_SYNC_SETTLED_MAX_DRIFT)
                return true;
            return now - g.LastMoverSyncAt >= MOVER_SYNC_SETTLED_MIN_INTERVAL;
        }

        private byte BuildBlingMoverFlags(float prevHeading, float prevX, float prevY, float heading, float posX, float posY, bool terminal)
        {
            byte flags = 0x00;
            if (terminal) flags |= 0x01;
            if (!NearlyEqual(prevHeading, heading)) flags |= 0x02;
            if (!terminal && (!NearlyEqual(prevX, posX) || !NearlyEqual(prevY, posY)))
            {
                flags |= 0x02;
                flags |= 0x04;
            }
            return (byte)(flags & 0x07);
        }

        private void WriteMoverRecord(LEWriter writer, byte flags, float heading, float posX, float posY)
        {
            writer.WriteByte((byte)(flags & 0x07));
            writer.WriteInt32(ToFixed(heading));
            writer.WriteInt32(ToFixed(posX));
            writer.WriteInt32(ToFixed(posY));
        }

        /// <summary>
        /// Send PlayAnimation action via 0x35/0x04 (CreateAction).
        /// This is how ALL bling gnome animations are triggered:
        /// spawn poof (180), fidget (101-104), pickup (110), active (140), death (150).
        /// 
        /// Format from Go: buildWorldUnitBehaviorPlayAnimationActionSubmsg
        /// Uses InterruptObjectAction with PlayAnimation class ID.
        /// </summary>
        private void WritePlayAnimationSubmsg(LEWriter writer, GnomeState g, ushort logicalId, byte animState, uint durationTicks)
        {
            byte wireState = animState;
            uint wireArg = logicalId;

            if (logicalId == ANIM_ACTIVE_SKILL)
            {
                wireState = ANIM_STATE_ACTIVE;
                wireArg = 40;
            }
            else if (logicalId >= ANIM_FIDGET_FIRST && logicalId <= ANIM_FIDGET_LAST)
            {
                wireState = ANIM_STATE_DIRECT;
                wireArg = logicalId;
            }

            writer.WriteByte(0x35);               // ComponentUpdate
            writer.WriteUInt16(g.BehaviorId);
            writer.WriteByte(0x04);               // CreateAction sub-type
            writer.WriteByte(0x20);               // PlayAnimation action class
            writer.WriteByte(0x00);               // state flag
            writer.WriteUInt32(wireState);        // animation state
            writer.WriteUInt32(wireArg);          // animation arg
            writer.WriteUInt32(durationTicks);    // duration in ticks
            writer.WriteUInt32(0x3F800000);       // speed = 1.0f (IEEE 754)
            WriteGnomeEntitySynchInfo(writer, g);
        }

        private void SendPlayAnimation(RRConnection conn, GnomeState g, ushort logicalId, byte animState, uint durationTicks)
        {
            if (!g.BehaviorBootstrapped) return;

            var writer = new LEWriter();
            writer.WriteByte(0x07); // BeginStream

            WritePlayAnimationSubmsg(writer, g, logicalId, animState, durationTicks);

            writer.WriteByte(0x06); // EndStream

            g.SendPacket(conn, 0x01, 0x0F, writer.ToArray());
        }

        /// <summary>
        /// Send ConvertItemsToGold native action (0xA1) on the behavior component.
        /// This triggers the client's built-in item bounce/convert visual sequence.
        /// Format from Go: buildWorldUnitBehaviorConvertItemsToGoldActionSubmsg
        /// </summary>
        private void SendConvertItemsToGoldAction(RRConnection conn, GnomeState g)
        {
            if (!g.BehaviorBootstrapped) return;

            ResolveOwnerAnchor(conn, out float ownerX, out float ownerY, out _, out _);
            int sourceX_fx = ToFixed(ownerX);
            int sourceY_fx = ToFixed(ownerY);
            int goldValueMod_fx = (int)(GOLD_VALUE_MOD * 256); // fixed 8.8

            var writer = new LEWriter();
            writer.WriteByte(0x07); // BeginStream

            writer.WriteByte(0x35);               // ComponentUpdate
            writer.WriteUInt16(g.BehaviorId);
            writer.WriteByte(0x04);               // CreateAction sub-type
            writer.WriteByte(0xA1);               // ConvertItemsToGold action class ID

            // ConvertItemsToGold readData (from Go: buildWorldConvertItemsToGoldActionReadData)
            writer.WriteByte(0x00);               // flags
            writer.WriteUInt32(5);                // version/arg count
            writer.WriteUInt16((ushort)CONVERT_SEARCH_RADIUS);
            writer.WriteInt32(sourceX_fx);
            writer.WriteInt32(sourceY_fx);
            writer.WriteInt32(goldValueMod_fx);

            WriteGnomeEntitySynchInfo(writer, g);
            writer.WriteByte(0x06); // EndStream

            g.SendPacket(conn, 0x01, 0x0F, writer.ToArray());
            Debug.LogError($"[GNOME-CONVERT] ConvertItemsToGold action sent, radius={CONVERT_SEARCH_RADIUS}, mod={GOLD_VALUE_MOD}");
        }

        // ═══════════════════════════════════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════════════════════════════════

        private void WriteGnomeEntitySynchInfo(LEWriter writer, GnomeState g)
        {
            uint hitPointsWire = g.HitPointsWire;
            if (hitPointsWire == 0)
                hitPointsWire = ResolveBlingGnomeHitPointsWire(1);

            writer.WriteByte(0x02);
            writer.WriteUInt32(hitPointsWire);
        }

        private ushort PickFidgetAnimation(GnomeState g)
        {
            ushort[] candidates = { ANIM_FIDGET_FIRST, 102, 103, ANIM_FIDGET_LAST };
            g.FidgetSerial++;

            int idx = _rng.Next(candidates.Length);
            ushort pick = candidates[idx];

            // Avoid repeating the same animation (Go logic)
            if (pick == g.LastFidgetAnim && candidates.Length > 1)
                pick = candidates[(idx + 1) % candidates.Length];

            g.LastFidgetAnim = pick;
            return pick;
        }

        private float GetFidgetDelay()
        {
            return IDLE_BASE_SECONDS + (float)(_rng.NextDouble() * IDLE_VARIABLE_SECONDS);
        }

        private float ComputeFollowStep(GnomeState g, float now)
        {
            float cadence = FOLLOW_DEFAULT_STEP_CADENCE;
            if (g.LastFollowRuntimeAt > 0f)
                cadence = Mathf.Max(0f, now - g.LastFollowRuntimeAt);
            if (cadence > FOLLOW_MAX_STEP_CADENCE)
                cadence = FOLLOW_MAX_STEP_CADENCE;
            float step = Mathf.Round(GNOME_SPEED * cadence);
            return Mathf.Max(1f, step);
        }

        private static void ProjectStep(float posX, float posY, float targetX, float targetY, float step, out float newX, out float newY)
        {
            float dx = targetX - posX;
            float dy = targetY - posY;
            float dist = Mathf.Sqrt(dx * dx + dy * dy);
            if (dist <= 0f || dist <= step)
            {
                newX = targetX;
                newY = targetY;
                return;
            }
            float scale = step / dist;
            newX = posX + dx * scale;
            newY = posY + dy * scale;
        }

        private static float HeadingFromDelta(float dx, float dy, float fallback)
        {
            if (NearlyEqual(dx, 0f) && NearlyEqual(dy, 0f))
                return fallback;
            float heading = Mathf.Round(Mathf.Atan2(-dx, dy) * Mathf.Rad2Deg);
            while (heading < 0f) heading += 360f;
            while (heading >= 360f) heading -= 360f;
            return heading;
        }

        private static bool NearlyEqual(float a, float b)
        {
            return Mathf.Abs(a - b) < 0.001f;
        }

        private static int ToFixed(float v) => (int)(v * 256f);
        private static int ToFixedRounded(float v) => v >= 0f ? (int)(v * 256f + 0.5f) : (int)(v * 256f - 0.5f);
        private static float DistSq(float ax, float ay, float bx, float by)
        {
            float dx = ax - bx, dy = ay - by;
            return dx * dx + dy * dy;
        }

        // ═══════════════════════════════════════════════════════════════════
        // CLEANUP
        // ═══════════════════════════════════════════════════════════════════

        public void OnPlayerDisconnect(int connId)
        {
            if (_gnomes.TryGetValue(connId, out var g))
            {
                if (g.AICoroutine != null) StopCoroutine(g.AICoroutine);
                if (g.ConversionCoroutine != null) StopCoroutine(g.ConversionCoroutine);
                _gnomes.Remove(connId);
            }
        }
    }
}
