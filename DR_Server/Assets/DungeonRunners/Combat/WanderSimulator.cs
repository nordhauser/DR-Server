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

        private List<WanderState> _entities = new List<WanderState>();
        private List<uint> _tickOrder = new List<uint>();

        private bool _defaultCanWander = true;
        public int EntityCount => _entities.Count;

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
                TargetX = monster.PosX,
                TargetY = monster.PosY
            };
            _entities.Add(state);
            _tickOrder.Add(monster.EntityId);
            Debug.LogError($"[WANDER-SIM] Registered monster {monster.EntityId} walk={monster.WalkSpeed:F1} range={monster.WanderRange:F1} (total: {_entities.Count})");
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

        /// <summary>
        /// Binary-exact Wander::update at 0x5314F0.
        /// </summary>
        private void TickEntity(WanderState ws, MersenneTwister rng)
        {
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
                    uint rawX = rng.Generate();  // 0x5316BE: X offset
                    uint rawY = rng.Generate();  // 0x5316DA: Y offset
                    if (ws.Monster != null && ws.CanWander && ws.Monster.WanderRange > 0f)
                    {
                        int range = Mathf.Max(1, Mathf.RoundToInt(ws.Monster.WanderRange));
                        uint span = (uint)Mathf.Max(1, range * 2);
                        ws.TargetX = ws.DefaultX + (int)(rawX % span) - range;
                        ws.TargetY = ws.DefaultY + (int)(rawY % span) - range;
                        if (!string.IsNullOrWhiteSpace(ws.Monster.ZoneName))
                        {
                            var pathMap = PathMapManager.Instance.GetPathMap(ws.Monster.ZoneName);
                            if (pathMap != null && !pathMap.CanReachPoint(ws.Monster.PosX, ws.Monster.PosY, ws.TargetX, ws.TargetY))
                                return;
                        }
                        ws.HasTarget = true;
                    }
                    else
                    {
                        ws.HasTarget = false;
                    }
                    // 0x53174D: mov byte ptr [ebp+0x75], 2
                    ws.State = 2;
                    break;

                case 2:
                    // 0x5315F4: Arrival check. No RNG.
                    // 0x53160E: call vtable[0x38] — isAtDestination()
                    // 0x531612: jne return — if still moving, stay in state 2
                    // 0x531618: mov byte ptr [ebp+0x75], 3 — if arrived, state=3
                    if (ws.Monster != null && ws.HasTarget)
                    {
                        float dx = ws.TargetX - ws.Monster.PosX;
                        float dy = ws.TargetY - ws.Monster.PosY;
                        float dist = Mathf.Sqrt(dx * dx + dy * dy);
                        float speed = ws.Monster.WalkSpeed > 0f ? ws.Monster.WalkSpeed : ws.Monster.MoveSpeed;
                        float step = speed / 30f;
                        if (dist <= step || dist <= 0.001f)
                        {
                            ws.Monster.PosX = ws.TargetX;
                            ws.Monster.PosY = ws.TargetY;
                            ws.HasTarget = false;
                            ws.State = 3;
                        }
                        else
                        {
                            ws.Monster.PosX += dx / dist * step;
                            ws.Monster.PosY += dy / dist * step;
                            ws.Monster.Heading = Mathf.Atan2(dy, dx) * Mathf.Rad2Deg;
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
                        uint raw = rng.Generate();
                        uint decision = raw % 150;
                        ushort timer = (ushort)(decision + 90);  // 0x5A = 90

                        if (ws.CanWander)
                        {
                            timer = (ushort)(timer * 3);  // 0x53157B: lea eax,[eax+eax*2]
                        }

                        ws.Timer = timer;
                        ws.State = 4;  // ALWAYS state 4, never state 1
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
                        uint raw = rng.Generate();
                        uint roll = raw % 100;

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
        public float TargetX;
        public float TargetY;
        public bool HasTarget;
    }
}
