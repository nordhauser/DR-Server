using System;
using System.Collections.Generic;
using DungeonRunners.Core;
using DungeonRunners.Utilities;
using UnityEngine;

namespace DungeonRunners.Combat
{
    /// <summary>
    /// Server-side mirror of the client's per-tick mover state machine. Phase 5 of
    /// Option 1-full. v0.5: scaffold + simple states (0=idle, 1=rotate, 3=rotate-secondary)
    /// + integration with <see cref="Pathfinder"/>. State 2 (path-following waypoint loop +
    /// replan) is stubbed and will be flushed out in a follow-up session.
    ///
    /// Field offsets (matched to client's <c>UnitMover</c> via Ghidra reverse 2026-05-27):
    /// <list type="table">
    ///   <item><term>+0x50</term><description>PosX (Fixed32)</description></item>
    ///   <item><term>+0x54</term><description>PosY (Fixed32)</description></item>
    ///   <item><term>+0x58</term><description>PosZ (Fixed32)</description></item>
    ///   <item><term>+0x5C</term><description>HeadingReset — restore value used by ClearMoveState</description></item>
    ///   <item><term>+0x60 bit2</term><description>HasReachedTarget flag</description></item>
    ///   <item><term>+0x61</term><description>MoveState byte (0=idle, 1=rotate primary, 2=path-follow, 3=rotate secondary)</description></item>
    ///   <item><term>+0x64</term><description>HeadingCurrent (Fixed32 angle, 0..0x16800 ≈ 2π)</description></item>
    ///   <item><term>+0x68</term><description>HeadingTargetSecondary (for state 3)</description></item>
    ///   <item><term>+0x6C</term><description>HeadingDelta per tick (rotation speed × dt)</description></item>
    ///   <item><term>+0x70</term><description>PathRequestId (-1 = none pending)</description></item>
    ///   <item><term>+0x74</term><description>ReplanCooldown (byte; decrements per tick; 0 = ready)</description></item>
    ///   <item><term>+0x7C..+0x80</term><description>Waypoint list (each = (X,Y) Fixed32 pair)</description></item>
    ///   <item><term>+0x8C</term><description>SteeringAccumX</description></item>
    ///   <item><term>+0x90</term><description>SteeringAccumY</description></item>
    /// </list>
    /// </summary>
    public sealed class UnitMoverSim
    {
        // Fixed32 angle constants (match Fixed32Math).
        private const int TwoPiRaw = 0x16800;
        private const int HalfPiRaw = TwoPiRaw / 4;

        /// <summary>Mover state machine values, byte +0x61 in client.</summary>
        public enum MoveStateEnum : byte
        {
            Idle = 0,
            RotatePrimary = 1,
            PathFollow = 2,
            RotateSecondary = 3,
        }

        // ── State fields (named for readability; bytes mapped via comments) ──

        /// <summary>Current world position (Fixed32 raw). +0x50/+0x54/+0x58.</summary>
        public Fixed32 PosX, PosY, PosZ;

        /// <summary>Restore heading for ClearMoveState. +0x5C.</summary>
        public int HeadingReset;

        /// <summary>+0x60. Bit 2 = HasReachedTargetSinceLastSet, cleared by ClearMoveState.</summary>
        public byte Flags;

        /// <summary>+0x61. Current state machine state.</summary>
        public MoveStateEnum State;

        /// <summary>+0x64. Current heading (Fixed32 angle, 0..0x16800).</summary>
        public int HeadingCurrent;

        /// <summary>+0x68. Secondary heading target (used by state 3).</summary>
        public int HeadingTargetSecondary;

        /// <summary>+0x6C. Heading delta added per tick.</summary>
        public int HeadingDelta;

        /// <summary>+0x70. Current path request ID, -1 if none pending.</summary>
        public int PathRequestId = -1;

        /// <summary>+0x74. Replan cooldown countdown (frames).</summary>
        public byte ReplanCooldown;

        /// <summary>+0x7C..+0x80. Waypoint list (game-units in Fixed32). Last entry = final goal.</summary>
        public List<(int X, int Y)> Waypoints = new List<(int X, int Y)>();

        /// <summary>+0x8C/+0x90. Steering accumulator.</summary>
        public int SteeringAccumX, SteeringAccumY;

        /// <summary>External PathMap reference (read for slide + reachability checks).</summary>
        public PathMap PathMap;

        /// <summary>External Pathfinder instance used for path requests. v0.5: synchronous.</summary>
        public Pathfinder Pathfinder;

        /// <summary>
        /// Per-tick movement speed in Fixed32 raw (world-units × 256 per tick).
        /// Caller sets from the mob's WalkSpeed field divided by tickrate. v0.6 default 0.
        /// </summary>
        public Fixed32 Speed;

        /// <summary>
        /// Arrive radius in Fixed32 raw. When the squared distance to the current waypoint
        /// drops below ArriveRadius², we pop the waypoint. v0.5 collapses client's dual
        /// inner/outer radii (+0x44 / +0x4C) to a single threshold.
        /// </summary>
        public Fixed32 ArriveRadius = Fixed32.FromInt(5);

        /// <summary>
        /// Called when the mob arrives at its final waypoint (path drained). Mirrors client
        /// vtable[0x34] invocation at the end of UpdateSteering's state-2.
        /// </summary>
        public System.Action OnArrived;

        /// <summary>
        /// Original goal coordinates (last waypoint at time of MoveToPoint call). Used by
        /// state-2 replan to re-issue path request to the same goal after obstruction.
        /// </summary>
        private int _goalX, _goalY;
        private bool _goalSet;

        // PA1.2: chase-target tracking. SetChaseTarget uses these to decide whether to
        // replan toward a moving target (player) or keep the existing path. Replans only
        // when the target has drifted past _chaseReplanThresholdSq raw units OR the
        // per-call cooldown has expired. Keeps replan churn bounded under fast kiting.
        private int _chaseLastPlannedGoalX, _chaseLastPlannedGoalY;
        private bool _chaseLastPlannedGoalSet;
        private byte _chaseReplanCooldownTicks;

        // ── Entry: per-tick update ──────────────────────────────────────────

        /// <summary>
        /// Mirrors <c>UnitMover::UpdateMovement @ 0x00536360</c>. Per-tick entry point.
        /// Calls <see cref="UpdateSteering"/> then <see cref="MoveUnit"/>.
        /// </summary>
        public void UpdateMovement()
        {
            UpdateSteering();
            MoveUnit();
        }

        /// <summary>
        /// Mirrors <c>UnitMover::UpdateSteering @ 0x00536380</c>. State-machine dispatch:
        /// <list type="bullet">
        ///   <item>State 0 (Idle): no-op.</item>
        ///   <item>State 1 (RotatePrimary): rotate <see cref="HeadingCurrent"/> by <see cref="HeadingDelta"/>, normalize to [0, 0x16800).</item>
        ///   <item>State 3 (RotateSecondary): same as state 1 but stores into <see cref="HeadingTargetSecondary"/> and mirrors into <see cref="HeadingCurrent"/>.</item>
        ///   <item>State 2 (PathFollow): waypoint pop / heading recompute / replan. <b>v0.5 STUB</b>.</item>
        /// </list>
        /// </summary>
        public void UpdateSteering()
        {
            switch (State)
            {
                case MoveStateEnum.Idle:
                    return;

                case MoveStateEnum.RotatePrimary:
                    HeadingCurrent = NormalizeAngle(HeadingCurrent + HeadingDelta);
                    return;

                case MoveStateEnum.RotateSecondary:
                    HeadingTargetSecondary = NormalizeAngle(HeadingTargetSecondary + HeadingDelta);
                    HeadingCurrent = HeadingTargetSecondary;
                    return;

                case MoveStateEnum.PathFollow:
                    UpdateSteeringPathFollowStub();
                    return;
            }
        }

        /// <summary>
        /// State-2 path-follow logic. Ports the bulk of <c>UnitMover::UpdateSteering @ 0x00536380</c>
        /// for the path-follow branch. v0.6: full pop-waypoint / replan / recompute-heading loop.
        /// Simplifications vs client:
        /// <list type="bullet">
        ///   <item>Single ArriveRadius (client has inner/outer dual radii at +0x44/+0x4C).</item>
        ///   <item>Skip-ahead via CastGroundRay (look-ahead to N+1th waypoint) not implemented.</item>
        ///   <item>Replan re-issues path to the original <see cref="_goalX"/>/<see cref="_goalY"/>;
        ///     client re-issues to waypoints[0] from the existing vector.</item>
        /// </list>
        /// </summary>
        private void UpdateSteeringPathFollowStub()
        {
            // Path request in flight — hold heading.
            if (PathRequestId != -1) return;

            // Pop any zero-distance waypoints (already on top of them).
            while (Waypoints.Count > 0)
            {
                var wp = Waypoints[Waypoints.Count - 1];
                long dx0 = wp.X - PosX.RawValue;
                long dy0 = wp.Y - PosY.RawValue;
                if (dx0 * dx0 + dy0 * dy0 != 0) break;
                Flags = (byte)(Flags & 0xFB);
                Waypoints.RemoveAt(Waypoints.Count - 1);
            }

            if (Waypoints.Count == 0)
            {
                ReplanCooldown = 0;
                State = MoveStateEnum.Idle;
                OnArrived?.Invoke();
                return;
            }

            var target = Waypoints[Waypoints.Count - 1];
            int deltaXRaw = target.X - PosX.RawValue;
            int deltaYRaw = target.Y - PosY.RawValue;
            long lenSq = (long)deltaXRaw * deltaXRaw + (long)deltaYRaw * deltaYRaw;

            // Heading toward this waypoint.
            HeadingCurrent = Fixed32Math.HeadingFromVector(deltaXRaw, deltaYRaw).RawValue;

            // Replan check: is the goal still reachable from the *original* request endpoint?
            if (PathMap != null && _goalSet)
            {
                bool reachable = PathMap.IsWalkable(
                    _goalX / (float)Fixed32.OneRaw,
                    _goalY / (float)Fixed32.OneRaw);
                if (reachable)
                {
                    ReplanCooldown = 0;
                }
                else if (ReplanCooldown == 0)
                {
                    ReplanCooldown = 0xF;
                }
                else
                {
                    ReplanCooldown = (byte)(ReplanCooldown - 1);
                    if (ReplanCooldown == 0)
                    {
                        ReissuePathRequest();
                        if (PathRequestId != -1) return;
                    }
                }
            }

            // Arrival check: did we reach this waypoint?
            long arriveRaw = ArriveRadius.RawValue;
            long arriveRadiusSq = arriveRaw * arriveRaw;
            bool arrived = lenSq <= arriveRadiusSq;
            if (arrived) Flags |= 0x04;
            else Flags = (byte)(Flags & 0xFB);

            if (!arrived) return;

            // Pop reached waypoint and carry over delta into steering accum.
            SteeringAccumX += deltaXRaw;
            SteeringAccumY += deltaYRaw;
            Flags = (byte)(Flags & 0xFB);
            Waypoints.RemoveAt(Waypoints.Count - 1);

            if (Waypoints.Count > 0)
            {
                var next = Waypoints[Waypoints.Count - 1];
                HeadingCurrent = Fixed32Math.HeadingFromVector(
                    next.X - PosX.RawValue,
                    next.Y - PosY.RawValue).RawValue;
            }
            else
            {
                ReplanCooldown = 0;
                State = MoveStateEnum.Idle;
                OnArrived?.Invoke();
            }
        }

        private void ReissuePathRequest()
        {
            if (Pathfinder == null || PathMap == null || !_goalSet) return;
            var startNode = PathMap.GetNodeAtWorld(PosX.ToFloat(), PosY.ToFloat());
            var goalNode = PathMap.GetNodeAtWorld(
                _goalX / (float)Fixed32.OneRaw,
                _goalY / (float)Fixed32.OneRaw);
            if (startNode == null || goalNode == null) return;
            Pathfinder.RequestPath(startNode, goalNode);
            while (!Pathfinder.IsDone && Pathfinder.NodesExpanded < 32768)
                Pathfinder.UpdateRequest(64);
            if (Pathfinder.GetPath(out var waypointNodes))
            {
                OnPathRequestComplete(0, waypointNodes);
            }
        }

        /// <summary>
        /// Mirrors <c>UnitMover::MoveUnit @ 0x005366C0</c>. Integrates heading × speed into
        /// PosX/PosY via <see cref="ResolveMovement"/> (PathMap-based collision slide).
        /// </summary>
        public void MoveUnit()
        {
            if (State != MoveStateEnum.PathFollow && State != MoveStateEnum.RotatePrimary && State != MoveStateEnum.RotateSecondary)
                return;
            if (Speed.RawValue == 0) return;

            // Facing vector (unit length in Fixed32) from current heading.
            var (vx, vy) = Fixed32Math.UnitVectorFromHeading(new Fixed32(HeadingCurrent));

            // Step = facing × speed (Fixed32 multiply). For unit vector × Fixed32 speed,
            // standard 64-bit signed multiply >> 8 (matches Phase 1 Fixed32 convention).
            int stepX = (int)(((long)vx.RawValue * Speed.RawValue) >> 8);
            int stepY = (int)(((long)vy.RawValue * Speed.RawValue) >> 8);

            ResolveMovement(stepX, stepY);
        }

        /// <summary>
        /// Mirrors <c>UnitMover::ResolveMovement @ 0x00536870</c>. Applies a delta to position
        /// with PathMap-based collision slide. Returns true if any movement occurred.
        ///
        /// v0.7 implementation — simplified axis-aligned slide vs client's
        /// <c>CastGroundRaySlide</c> (which uses 8 directional perpendicular tangents):
        /// <list type="number">
        ///   <item>Try the full diagonal step. If destination is walkable, commit.</item>
        ///   <item>Else try X-only slide. If walkable, commit X movement.</item>
        ///   <item>Else try Y-only slide. If walkable, commit Y movement.</item>
        ///   <item>Else fully blocked: no movement.</item>
        /// </list>
        /// If <see cref="PathMap"/> is null, commits unconditionally (no collision check).
        /// </summary>
        public bool ResolveMovement(int dxRaw, int dyRaw)
        {
            if (PathMap == null)
            {
                PosX = new Fixed32(PosX.RawValue + dxRaw);
                PosY = new Fixed32(PosY.RawValue + dyRaw);
                return true;
            }

            // Sub-step to avoid tunneling through walls when dx/dy exceeds the PathMap
            // node resolution. Step granularity = half the node spacing (5 world units
            // for the default 10-unit PathMap). Each sub-step is independent: if the
            // destination is blocked the slide kicks in for that sub-step only.
            float maxAxisStep = (PathMap.NodeResolution * 0.5f);
            int maxStepRaw = Fixed32.FromFloat(maxAxisStep).RawValue;
            int axisMag = Math.Max(Math.Abs(dxRaw), Math.Abs(dyRaw));
            int subSteps = Math.Max(1, (axisMag + maxStepRaw - 1) / maxStepRaw);

            int dxSub = dxRaw / subSteps;
            int dySub = dyRaw / subSteps;
            int dxRem = dxRaw - dxSub * subSteps;
            int dyRem = dyRaw - dySub * subSteps;

            bool anyMoved = false;
            for (int i = 0; i < subSteps; i++)
            {
                int sx = dxSub + (i == 0 ? dxRem : 0);
                int sy = dySub + (i == 0 ? dyRem : 0);
                if (DoSingleStep(sx, sy)) anyMoved = true;
                else break;
            }
            return anyMoved;
        }

        private bool DoSingleStep(int dxRaw, int dyRaw)
        {
            float curX = PosX.ToFloat();
            float curY = PosY.ToFloat();
            float proposedX = curX + dxRaw / (float)Fixed32.OneRaw;
            float proposedY = curY + dyRaw / (float)Fixed32.OneRaw;

            if (PathMap.IsWalkable(proposedX, proposedY))
            {
                PosX = new Fixed32(PosX.RawValue + dxRaw);
                PosY = new Fixed32(PosY.RawValue + dyRaw);
                return true;
            }

            // Try X-only slide (preserve Y).
            if (dxRaw != 0 && PathMap.IsWalkable(proposedX, curY))
            {
                PosX = new Fixed32(PosX.RawValue + dxRaw);
                return true;
            }

            // Try Y-only slide (preserve X).
            if (dyRaw != 0 && PathMap.IsWalkable(curX, proposedY))
            {
                PosY = new Fixed32(PosY.RawValue + dyRaw);
                return true;
            }

            return false;
        }

        // ── State transitions ───────────────────────────────────────────────

        /// <summary>
        /// Mirrors <c>UnitMover::MoveToPoint @ 0x00535FB0</c>. Issues a path request to
        /// the configured <see cref="Pathfinder"/>. If target equals current position
        /// (already there) or the active state-2 first waypoint matches, this is a no-op.
        /// </summary>
        public void MoveToPoint(int targetX, int targetY)
        {
            // No-op if already at target.
            if (PosX.RawValue == targetX && PosY.RawValue == targetY) return;

            // No-op if already path-following to this exact target (first waypoint matches).
            if (State == MoveStateEnum.PathFollow
                && Waypoints.Count > 0
                && Waypoints[0].X == targetX
                && Waypoints[0].Y == targetY)
            {
                return;
            }

            ClearMoveState();
            State = MoveStateEnum.PathFollow;
            _goalX = targetX;
            _goalY = targetY;
            _goalSet = true;

            // v0.5: issue path request synchronously if Pathfinder is available.
            if (Pathfinder != null && PathMap != null)
            {
                var startNode = PathMap.GetNodeAtWorld(
                    PosX.ToFloat(),
                    PosY.ToFloat());
                var goalNode = PathMap.GetNodeAtWorld(
                    targetX / (float)Fixed32.OneRaw,
                    targetY / (float)Fixed32.OneRaw);

                if (startNode != null && goalNode != null)
                {
                    Pathfinder.RequestPath(startNode, goalNode);
                    while (!Pathfinder.IsDone && Pathfinder.NodesExpanded < 32768)
                        Pathfinder.UpdateRequest(64);

                    if (Pathfinder.GetPath(out var waypointNodes))
                    {
                        OnPathRequestComplete(0, waypointNodes);
                        return;
                    }
                }
            }

            // Fallback: Pathfinder unavailable or failed. Walk straight-line toward target
            // (matches legacy WanderSimulator behavior). This protects against PathMap
            // divergence where our PathMap differs from the client's — sim still moves so
            // shadow-mode drift stays bounded.
            Waypoints.Clear();
            Waypoints.Add((targetX, targetY));
        }

        /// <summary>
        /// Mirrors <c>UnitMover::OnPathRequestComplete @ 0x005369B0</c>. Callback for a
        /// completed path request — refills the waypoint list from the result.
        /// </summary>
        public void OnPathRequestComplete(int requestId, List<PathNode> pathWaypoints)
        {
            PathRequestId = -1;
            Waypoints.Clear();
            if (pathWaypoints == null || pathWaypoints.Count == 0) return;
            // Pathfinder.GetPath returns waypoints in start→goal order with the start node
            // at index 0. Mob is at (or near) the start position, so the start node would
            // either pop immediately (zero distance) or waste ticks back-tracking to it
            // (grid resolution mismatch). Skip it. We also reverse so that end-of-list
            // is the next waypoint to consume — matching client's vector usage pattern.
            for (int i = pathWaypoints.Count - 1; i >= 1; i--)
            {
                var n = pathWaypoints[i];
                Waypoints.Add((Fixed32.FromFloat(n.WorldX).RawValue,
                               Fixed32.FromFloat(n.WorldY).RawValue));
            }
        }

        /// <summary>
        /// Mirrors <c>UnitMover::ClearMoveState @ 0x00536930</c>. Resets state machine,
        /// heading, waypoint list, and cancels any pending path request.
        /// </summary>
        public void ClearMoveState()
        {
            Flags &= 0xFB;                  // clear bit 2 (HasReachedTarget)
            State = MoveStateEnum.Idle;
            HeadingCurrent = HeadingReset;
            HeadingTargetSecondary = HeadingReset;
            HeadingDelta = 0;
            Waypoints.Clear();
            // PathRequestId cancellation: in our synchronous v0.5 model there's no async
            // path-manager queue to notify, so we just reset the ID.
            PathRequestId = -1;
            ReplanCooldown = 0;
            _goalSet = false;
            SteeringAccumX = 0;
            SteeringAccumY = 0;
            _chaseLastPlannedGoalSet = false;
            _chaseReplanCooldownTicks = 0;
        }

        /// <summary>
        /// PA1.2: chase-aware wrapper around <see cref="MoveToPoint"/>. Designed to be
        /// called every chase tick with the current target position. Replans only when:
        /// <list type="bullet">
        ///   <item>first call after entering chase (no prior planned goal), OR</item>
        ///   <item>state isn't PathFollow (mover went idle / was reset), OR</item>
        ///   <item>target has drifted past <paramref name="replanThresholdRaw"/> from the last planned goal, OR</item>
        ///   <item>per-call cooldown <paramref name="replanCooldownTicks"/> has expired since last replan.</item>
        /// </list>
        /// Between replans the existing waypoint path keeps driving heading; the mob
        /// follows yesterday's path toward yesterday's target. The cooldown bounds
        /// pathfinder work at ~1 call per replanCooldownTicks even under aggressive kiting.
        /// <para>
        /// Coordinates are in Fixed32 raw units (use <c>Fixed32.FromFloat(worldUnits).RawValue</c>).
        /// </para>
        /// </summary>
        public void SetChaseTarget(int targetXRaw, int targetYRaw, int arriveRadiusRaw,
                                   byte replanCooldownTicks, int replanThresholdRaw)
        {
            // Stop-radius for the chase: pathfinder pops the goal waypoint once we're
            // within this distance. Caller sets it to the mob's attack range so the
            // mover halts cleanly at melee range instead of walking into the player.
            ArriveRadius = new Fixed32(arriveRadiusRaw);

            bool needsReplan = false;
            if (!_chaseLastPlannedGoalSet || State != MoveStateEnum.PathFollow || Waypoints.Count == 0)
            {
                needsReplan = true;
            }
            else
            {
                long dx = (long)targetXRaw - _chaseLastPlannedGoalX;
                long dy = (long)targetYRaw - _chaseLastPlannedGoalY;
                long distSq = dx * dx + dy * dy;
                long thresholdSq = (long)replanThresholdRaw * replanThresholdRaw;
                if (distSq > thresholdSq)
                {
                    needsReplan = true;
                }
                else if (_chaseReplanCooldownTicks > 0)
                {
                    _chaseReplanCooldownTicks--;
                }
                else
                {
                    needsReplan = true;
                }
            }

            if (!needsReplan) return;

            // PA1.5b: chase uses a single straight-line waypoint at the target —
            // NOT Pathfinder-routed waypoints. Reasoning: legacy chase math takes
            // a straight `dx/dist * step` toward the player, not through grid nodes.
            // Pathfinder waypoints make the mover's heading aim at intermediate
            // grid cells, which creates ~0.1 unit mid-chase drift from legacy.
            // For byte-exact parity (the Path α goal), we bypass MoveToPoint
            // entirely and seed the waypoint list directly. Wall-routing during
            // chase is a TODO for a future fix — current legacy chase also
            // doesn't route around walls; it just stops when path-blocked.
            ClearMoveState();
            State = MoveStateEnum.PathFollow;
            _goalX = targetXRaw;
            _goalY = targetYRaw;
            _goalSet = true;
            Waypoints.Clear();
            Waypoints.Add((targetXRaw, targetYRaw));

            _chaseLastPlannedGoalX = targetXRaw;
            _chaseLastPlannedGoalY = targetYRaw;
            _chaseLastPlannedGoalSet = true;
            _chaseReplanCooldownTicks = replanCooldownTicks;
        }

        /// <summary>
        /// PA1.2: clear chase tracking without disturbing mover state. Called when the
        /// consumer transitions out of Chase (e.g. into Combat or Return). The mover's
        /// own state stays as-is so any in-flight waypoint can drain naturally; the
        /// chase fields just stop trying to replan toward the old target.
        /// </summary>
        public void EndChaseTracking()
        {
            _chaseLastPlannedGoalSet = false;
            _chaseReplanCooldownTicks = 0;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Normalize a Fixed32 angle to [0, 0x16800) (≈ [0, 2π)). Matches client's
        /// in-place normalization in <c>UpdateSteering</c>.
        /// </summary>
        public static int NormalizeAngle(int angle)
        {
            if (angle < 0)
            {
                while (angle < 0) angle += TwoPiRaw;
                return angle;
            }
            if (angle >= TwoPiRaw)
            {
                while (angle >= TwoPiRaw) angle -= TwoPiRaw;
                return angle;
            }
            return angle;
        }
    }
}
