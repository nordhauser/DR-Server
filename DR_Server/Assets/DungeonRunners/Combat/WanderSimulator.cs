using System;
using System.Collections.Generic;
using DungeonRunners.Core;
using UnityEngine;

namespace DungeonRunners.Combat
{
    /// <summary>
    /// Replicates client's Wander::update state machine for RNG synchronization.
    /// ALL values from binary disassembly at 0x5314F0 (jump table at 0x531760).
    /// 
    /// State machine (binary-proven):
    ///   State 0 (0x531513): Set state=3. No RNG.
    ///   State 1 (0x531624): 2 RNG ALWAYS (X,Y offset). Then state=2.
    ///   State 2 (0x5315F4): vtable[0x38] arrival check. No RNG. If arrived → state=3.
    ///   State 3 (0x53151F): 1 RNG: Generate()%150. Timer = (result+90). If canWander: timer*=3. ALWAYS → state=4.
    ///   State 4 (0x53158E): Timer countdown. When 0: if canWander: 1 RNG %100, &lt;30 → state=1 (30%), else timer=450.
    /// </summary>
    public class WanderSimulator
    {
        private static WanderSimulator _instance;
        public static WanderSimulator Instance => _instance ??= new WanderSimulator();
        private static readonly bool VerboseWanderLogs = IsTruthy(Environment.GetEnvironmentVariable("DR_SERVER_VERBOSE_WANDER_LOGS"));

        private List<WanderState> _entities = new List<WanderState>();
        private List<uint> _tickOrder = new List<uint>();

        private bool _defaultCanWander = true;
        public int EntityCount => _entities.Count;

        private static bool IsTruthy(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            switch (value.Trim().ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "on":
                    return true;
                default:
                    return false;
            }
        }

        private static void LogVerboseWander(string message)
        {
            if (VerboseWanderLogs)
                Debug.LogError(message);
        }

        public void RegisterEntity(uint entityId, bool canWander = true)
        {
            UnregisterEntity(entityId);
            var state = new WanderState
            {
                EntityId = entityId,
                State = 0,
                Timer = 0,
                CanWander = canWander
            };
            _entities.Add(state);
            _tickOrder.Add(entityId);
            Debug.LogError($"[WANDER-SIM] Registered entity {entityId} (total: {_entities.Count})");
        }

        public void RegisterMonster(Monster monster, bool canWander = true)
        {
            if (monster == null) return;
            UnregisterEntity(monster.EntityId);
            var state = new WanderState
            {
                EntityId = monster.EntityId,
                Monster = monster,
                State = 0,
                Timer = 0,
                CanWander = canWander,
                DefaultX = monster.SpawnPosX,
                DefaultY = monster.SpawnPosY,
                ClientX = monster.PosX,
                ClientY = monster.PosY,
                TargetX = monster.PosX,
                TargetY = monster.PosY
            };
            _entities.Add(state);
            _tickOrder.Add(monster.EntityId);
            Debug.LogError($"[WANDER-SIM] Registered monster {monster.EntityId} walk={monster.WalkSpeed:F1} range={monster.WanderRange:F1} canWander={canWander} (total: {_entities.Count})");
        }

        public void UnregisterEntity(uint entityId)
        {
            _entities.RemoveAll(e => e.EntityId == entityId);
            _tickOrder.Remove(entityId);
        }

        public void TickAll(MersenneTwister rng)
        {
            for (int i = 0; i < _entities.Count; i++)
            {
                TickEntity(_entities[i], rng);
            }
        }

        public void TickEntity(uint entityId, MersenneTwister rng)
        {
            for (int i = 0; i < _entities.Count; i++)
            {
                if (_entities[i].EntityId != entityId)
                    continue;
                TickEntity(_entities[i], rng);
                return;
            }
        }

        public bool TryGetClientVisiblePosition(uint entityId, out float posX, out float posY)
        {
            for (int i = 0; i < _entities.Count; i++)
            {
                var state = _entities[i];
                if (state.EntityId != entityId)
                    continue;

                posX = state.ClientX;
                posY = state.ClientY;
                return true;
            }

            posX = 0f;
            posY = 0f;
            return false;
        }

        /// <summary>
        /// Binary Wander::update at 0x5314F0, with movement kept as client-visible
        /// simulation state until the native UnitMover packet contract is closed.
        /// </summary>
        private void TickEntity(WanderState ws, MersenneTwister rng)
        {
            if (ws.Monster != null && (!ws.Monster.IsAlive || ws.Monster.State != MonsterState.Idle))
                return;

            switch (ws.State)
            {
                case 0:
                    // 0x531513: mov byte ptr [ebp+0x75], 3 — set state=3, return. No RNG.
                    ws.State = 3;
                    break;

                case 1:
                    // 0x531624: Movement tick.
                    // Binary: BOTH canWander and !canWander paths reach Generate() calls.
                    // 0x531699: cmp [ebp+0x74],0 — canWander check
                    // 0x53169D: je 0x531759 — if !canWander, jump to 0x531759
                    // 0x531759: xor bl,bl; jmp 0x5316B7 — still reaches RNG section
                    // 0x5316BE: call Generate() — RNG #1 (X offset)
                    // 0x5316DA: call Generate() — RNG #2 (Y offset)
                    // ALWAYS 2 RNG regardless of canWander.
                    int rngBeforeTarget = rng.CallsSinceReseed;
                    uint rawX = rng.Generate();  // 0x5316BE: X offset
                    uint rawY = rng.Generate();  // 0x5316DA: Y offset
                    LogVerboseWander($"[WANDER-RNG] entity={ws.EntityId} state=1 target rawX=0x{rawX:X8} rawY=0x{rawY:X8} rng={rngBeforeTarget}->{rng.CallsSinceReseed}");
                    if (ws.Monster != null && ws.Monster.WanderRange > 0f)
                    {
                        int range = Mathf.Max(1, Mathf.RoundToInt(ws.Monster.WanderRange));
                        uint span = (uint)Mathf.Max(1, range * 2);
                        float baseX = ws.CanWander ? ws.DefaultX : ws.ClientX;
                        float baseY = ws.CanWander ? ws.DefaultY : ws.ClientY;
                        ws.TargetX = baseX + (int)(rawX % span) - range;
                        ws.TargetY = baseY + (int)(rawY % span) - range;
                        ws.TargetAttempt++;
                        bool pathValid = true;
                        if (ws.CanWander && !string.IsNullOrWhiteSpace(ws.Monster.ZoneName))
                        {
                            var pathMap = PathMapManager.Instance.GetPathMap(ws.Monster.ZoneName);
                            if (pathMap != null && pathMap.TryCanReachPoint(ws.ClientX, ws.ClientY, ws.TargetX, ws.TargetY, out bool canReachPoint))
                                pathValid = canReachPoint;
                            if (!pathValid)
                            {
                                LogVerboseWander($"[WANDER-AUDIT] entity={ws.EntityId} state=1 attempt={ws.TargetAttempt} canWander={ws.CanWander} anchor=({baseX:F1},{baseY:F1}) current=({ws.ClientX:F1},{ws.ClientY:F1}) rawX=0x{rawX:X8} rawY=0x{rawY:X8} target=({ws.TargetX:F1},{ws.TargetY:F1}) pathValid=False accepted=False rng={rngBeforeTarget}->{rng.CallsSinceReseed}");
                                return;
                            }
                        }
                        float dx = ws.TargetX - ws.ClientX;
                        float dy = ws.TargetY - ws.ClientY;
                        float dist = Mathf.Sqrt(dx * dx + dy * dy);
                        float speed = ws.Monster.WalkSpeed > 0f ? ws.Monster.WalkSpeed : ws.Monster.MoveSpeed;
                        ws.MoveTicksRemaining = speed > 0f ? Mathf.Max(1, Mathf.CeilToInt(dist / speed * 30f)) : 1;
                        LogVerboseWander($"[WANDER-AUDIT] entity={ws.EntityId} state=1 attempt={ws.TargetAttempt} canWander={ws.CanWander} anchor=({baseX:F1},{baseY:F1}) current=({ws.ClientX:F1},{ws.ClientY:F1}) rawX=0x{rawX:X8} rawY=0x{rawY:X8} target=({ws.TargetX:F1},{ws.TargetY:F1}) pathValid={pathValid} accepted=True travelTicks={ws.MoveTicksRemaining} rng={rngBeforeTarget}->{rng.CallsSinceReseed}");
                        ws.HasTarget = true;
                    }
                    else
                    {
                        ws.HasTarget = false;
                        ws.MoveTicksRemaining = 1;
                    }
                    // 0x53174D: mov byte ptr [ebp+0x75], 2
                    ws.State = 2;
                    ws.TargetAttempt = 0;
                    break;

                case 2:
                    // 0x5315F4: Arrival check. No RNG.
                    // 0x53160E: call vtable[0x38] — isAtDestination()
                    // 0x531612: jne return — if still moving, stay in state 2
                    // 0x531618: mov byte ptr [ebp+0x75], 3 — if arrived, state=3
                    if (ws.Monster != null && ws.HasTarget)
                    {
                        float dx = ws.TargetX - ws.ClientX;
                        float dy = ws.TargetY - ws.ClientY;
                        float dist = Mathf.Sqrt(dx * dx + dy * dy);
                        float speed = ws.Monster.WalkSpeed > 0f ? ws.Monster.WalkSpeed : ws.Monster.MoveSpeed;
                        float step = speed / 30f;
                        if (ws.MoveTicksRemaining > 0)
                            ws.MoveTicksRemaining--;
                        if (ws.MoveTicksRemaining == 0 || dist <= step || dist <= 0.001f)
                        {
                            ws.ClientX = ws.TargetX;
                            ws.ClientY = ws.TargetY;
                            ws.HasTarget = false;
                            ws.State = 3;
                            LogVerboseWander($"[WANDER-MOVE] entity={ws.EntityId} arrived visual=({ws.ClientX:F1},{ws.ClientY:F1}) target=({ws.TargetX:F1},{ws.TargetY:F1})");
                        }
                        else
                        {
                            ws.ClientX += dx / dist * step;
                            ws.ClientY += dy / dist * step;
                        }
                    }
                    else
                    {
                        ws.ArriveTicks++;
                        if (ws.ArriveTicks >= 1)
                        {
                            ws.State = 3;
                            ws.ArriveTicks = 0;
                        }
                    }
                    break;

                case 3:
                    // 0x53151F: Wander decision.
                    // 0x531555: call Generate() — 1 RNG consumed ALWAYS
                    // 0x53155C-0x53156C: Generate() % 150 (via multiply-shift)
                    // 0x531572: lea eax, [ecx + 0x5A] — timer = (result % 150) + 90
                    // 0x53156E: cmp [ebp+0x74], 0 — canWander check
                    // 0x531579: je 0x531582 — if !canWander, skip multiply
                    // 0x53157B: lea eax, [eax + eax*2] — timer *= 3
                    // 0x531582: mov byte ptr [ebp+0x75], 4 — ALWAYS state=4
                    {
                        int rngBeforeTimer = rng.CallsSinceReseed;
                        uint raw = rng.Generate();
                        uint decision = raw % 150;
                        ushort timer = (ushort)(decision + 90);  // 0x5A = 90

                        if (ws.CanWander)
                        {
                            timer = (ushort)(timer * 3);  // 0x53157B: lea eax,[eax+eax*2]
                        }

                        ws.Timer = timer;
                        ws.State = 4;  // ALWAYS state 4, never state 1
                        LogVerboseWander($"[WANDER-RNG] entity={ws.EntityId} state=3 timer raw=0x{raw:X8} roll={decision} timer={timer} canWander={ws.CanWander} rng={rngBeforeTimer}->{rng.CallsSinceReseed}");
                    }
                    break;

                case 4:
                    // 0x53158E: Timer countdown.
                    // 0x531592-0x531598: if timer > 0, decrement and store
                    // 0x53159C-0x5315A1: if timer != 0, return (no RNG)
                    if (ws.Timer > 0)
                    {
                        ws.Timer--;
                    }
                    if (ws.Timer > 0)
                    {
                        // No RNG consumed while timer > 0
                        return;
                    }

                    // Timer expired (timer == 0)
                    // 0x5315A7: cmp [ebp+0x74], 0 — canWander check
                    // 0x5315AB: je 0x5315E8 — if !canWander → state=1 directly (no RNG)
                    if (!ws.CanWander)
                    {
                        ws.State = 1;
                        return;
                    }

                    // canWander path:
                    // 0x5315C4: call Generate() — 1 RNG
                    // 0x5315C9-0x5315D0: div by 100 → Generate() % 100
                    // 0x5315D2: cmp edx, 0x1E — compare remainder with 30
                    // 0x5315D5: jb 0x5315E8 — if < 30 (30%) → state=1
                    // 0x5315D7: mov edx, 0x1C2 — else timer = 450
                    {
                        int rngBeforeMoveCheck = rng.CallsSinceReseed;
                        uint raw = rng.Generate();
                        uint roll = raw % 100;
                        LogVerboseWander($"[WANDER-RNG] entity={ws.EntityId} state=4 move-check raw=0x{raw:X8} roll={roll} rng={rngBeforeMoveCheck}->{rng.CallsSinceReseed}");

                        if (roll < 30)  // 30% chance → move
                        {
                            ws.State = 1;
                        }
                        else  // 70% chance → stay idle
                        {
                            ws.Timer = 450;  // 0x1C2
                            // Stay in state 4
                        }
                    }
                    break;

                default:
                    ws.State = 3;
                    break;
            }
        }

        public void Clear()
        {
            _entities.Clear();
            _tickOrder.Clear();
        }

        public string DumpState()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[WANDER-SIM] {_entities.Count} entities:");
            int[] stateCounts = new int[5];
            foreach (var e in _entities)
            {
                if (e.State >= 0 && e.State <= 4) stateCounts[e.State]++;
            }
            sb.AppendLine($"  State0={stateCounts[0]} State1={stateCounts[1]} State2={stateCounts[2]} State3={stateCounts[3]} State4={stateCounts[4]}");
            return sb.ToString();
        }

        public string DescribeSchedule(int soonTicks = 30)
        {
            int state1Pending = 0;
            int movingState2 = 0;
            int state3Waiting = 0;
            int state4Due = 0;
            int state4Soon = 0;
            foreach (var e in _entities)
            {
                switch (e.State)
                {
                    case 1:
                        state1Pending++;
                        break;
                    case 2:
                        movingState2++;
                        break;
                    case 3:
                        state3Waiting++;
                        break;
                    case 4:
                        if (e.Timer == 0) state4Due++;
                        else if (e.Timer <= soonTicks) state4Soon++;
                        break;
                }
            }
            return $"state3Waiting={state3Waiting} state4Due={state4Due} state4Soon={state4Soon} state1Pending={state1Pending} movingState2={movingState2}";
        }
    }

    public class WanderState
    {
        public uint EntityId;
        public byte State;
        public ushort Timer;
        public bool CanWander;
        public int ArriveTicks;
        public Monster Monster;
        public float DefaultX;
        public float DefaultY;
        public float ClientX;
        public float ClientY;
        public float TargetX;
        public float TargetY;
        public bool HasTarget;
        public int TargetAttempt;
        public int MoveTicksRemaining;
    }
}
