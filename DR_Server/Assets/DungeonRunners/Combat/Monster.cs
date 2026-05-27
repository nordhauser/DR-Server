using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonRunners.Combat
{
    public class Monster
    {
        public uint EntityId;
        public uint BehaviorId;
        public uint SkillsId;
        public uint ManipulatorsId;
        public uint ModifiersId;
        public uint UnitId;
        public int UseTargetCount;
        public string GCType;
        public string BehaviourType;
        public string SpawnBehaviourType;
        public string Name;
        public string Faction;
        public string CreatureType;
        public string Element;
        public string Tier;
        public byte Level = 10;  // CRITICAL - must not be 0 or client crashes!
        public float Difficulty = 1.0f;  // From Tables.gc — XP multiplier (RECRUIT=1.0, VETERAN=2.0, etc)
        public float ExperienceDifficulty = 1.0f;
        public string SpawnGCType;  // dungeon-specific GC path for spawn packet
        public uint MaxHPWire;
        public uint CurrentHPWire;
        public float LastClientHPReportTime;
        public uint LastClientHPReportWire;
        public bool DeathPendingClientConfirmation;
        public float DeathPendingSince;
        public byte NativeDeathState;
        public ushort NativeCorpseTicksRemaining;
        public ushort NativeFadeTicksRemaining;
        public bool NativeDeathLifecycleActive;
        public bool NativeDeathRemoveSent;
        public uint MaxManaWire;
        public uint CurrentManaWire;
        public int BaseDamage;
        public float AttackRating = 1.0f;
        public float DamageMod = 1.0f;
        public float DamageTakenMod = 100f;
        public float DamageVolatility = 0.5f;
        public float WeaponDamage = 1.0f;
        public float HealthRegen = 0f;
        public bool HasAuthoredHealthRegen;
        public float ManaRegen = 0f;
        public bool HasAuthoredManaRegen;
        public float CritChance = 0f;
        public float DefenseRating = 1.0f;
        public float DivineResist = 0f;
        public float FireResist = 0f;
        public float IceResist = 0f;
        public float PoisonResist = 0f;
        public float ShadowResist = 0f;
        public float PosX, PosY, PosZ;
        public float SpawnPosX, SpawnPosY, SpawnPosZ;
        public byte SessionId;
        public float Heading;

        // PA1.1: Shared UnitMoverSim instance. Owned conceptually by the Monster;
        // assigned by WanderSimulator.RegisterMonster (which constructs it). Both
        // WanderSimulator (idle wander) and CombatManager.ProcessMonsterMovement
        // (active chase) read/write the same instance once PA1.3 lands. Null until
        // the mob is registered with WanderSimulator.
        public UnitMoverSim Mover;

        public float AggroRange = 50f;
        public float PerceptionRange = 0f;
        public float ShoutRange = 0f;
        public float LeashRange = 30f;
        public float AttackRange = 2.5f;
        public float ClientSyncTolerance = 10f;
        public float CollisionRadius = 5f;
        public string AttackType;
        public string IdleAction;
        public string LogicType;
        public string AttackStyle;
        public bool Retreatable;
        public bool Leashed;
        public bool UseIdleTime;
        public bool AutoScan;
        public bool AvoidUnits;
        public bool TurnBeforeMoving;
        public bool PlayerControlled;
        public int CollisionBand;
        public int CollisionPriority;
        public float ScanFrequency;
        public float FleeRange;
        public float RetreatRangeSquared;
        public float TeleportFrequency;
        public float TeleportLimboTime;
        public float BaseTime;
        public float VariableTime;
        public ushort CorpseLingerTicks = 900;
        public bool AutoRespawn = false;
        public ushort RespawnRateTicks = 3600;
        public float AttackSpeed = 1.0f;
        public float AttackCooldown = 1.5f;
        public float AttackLeadDelay = 0.75f;
        public float MoveSpeed = 5f;
        public float WalkSpeed = 25f;
        public float WanderRange = 0f;
        public uint LastStateCounter;

        // Two-stage aggro (matches client's [Desc+0x86]==2 alerted runtime byte):
        // Default false. Becomes true when a nearby aggro'd mob shouts within ShoutRange.
        // When true, ResolveMonsterTargetSearchRange returns ShoutRange instead of AggroRange,
        // letting this mob aggro the player from greater distance (chain-aggro mechanic).
        // Reset to false when the mob loses aggro (lose-target / leash / etc).
        public bool AlertedByShout { get; set; }

        // Per-entity RNG for deterministic combat (seeded via opcode 0x0C at spawn)
        public uint RngSeed;
        public MersenneTwister Rng;

        // Manipulators from creature database
        public Dictionary<string, ManipulatorData> Manipulators;
        public bool AggroSent { get; set; }

        // UpdateNumber tracking for HP synch - client counts every synch update
        // and expects this counter packed into low byte of HP value
        public byte UpdateNumber = 0;

        private volatile int _stateInt = (int)MonsterState.Idle;
        public MonsterState State
        {
            get => (MonsterState)_stateInt;
            set => _stateInt = (int)value;
        }
        public bool IsAlive = true;
        private volatile uint _targetId = 0;
        public uint TargetId
        {
            get => _targetId;
            set => _targetId = value;
        }
        public float LastAttackTime;
        public bool AttackPending;
        public float AttackCommitTime;
        public float AttackSoundTime;
        public float AttackSoundLeadDelay;
        public bool AttackSoundPending;
        public bool HasAttackSound;
        public uint AttackSoundRaw;
        public uint AttackSoundGateRaw;
        public uint AttackSoundRepeatRaw;
        public byte AttackSessionId;
        public byte AttackAnimationIndex;
        public uint AttackUseRaw;
        public uint AttackSearchTargetId;
        public uint AttackSearchRaw;
        public uint AttackSearchTieRaw;
        public bool AttackClientVisible;
        public bool AttackNativeContactOnly;
        public bool AttackHitResolved;
        public bool UsePrimaryActiveSkillThisAttack;
        public string PrimaryActiveSkillPath;
        public byte PrimaryActiveSkillId = 10;
        public float PrimaryActiveSkillRange;
        public float PrimaryActiveSkillCooldownSeconds;
        public ushort PrimaryActiveSkillCooldownTicks;
        public ushort PrimaryActiveSkillCooldownRemainingTicks;
        public float PrimaryActiveSkillCooldownLastTime;
        public int PrimaryActiveSkillAnimationId;
        public string PrimaryActiveSkillEffect;
        public string PrimaryActiveSkillCastModifier;
        public float AttackStartedTime;
        public float AttackEndTime;
        public int AttackWeaponSoundCount;
        public int AttackRepeatSoundCount;
        public int[] AttackTotalFrames = new int[] { 30, 30, 30 };
        public int[] AttackHitFrames = new int[] { 15, 15, 15 };
        public int[] AttackSoundFrames = new int[] { 10, 10, 10 };
        public float AttackCommitTargetX;
        public float AttackCommitTargetY;
        public uint CombatContactTargetId;
        public float CombatContactUntil;
        public uint AlertSourceEntityId;
        public float SpawnTime;  // Time.time when spawned - skip first move packet
        private volatile bool _aggroTriggered = false;
        public bool AggroTriggered
        {
            get => _aggroTriggered;
            set => _aggroTriggered = value;
        }
        private Dictionary<uint, int> _threatTable = new Dictionary<uint, int>();
        public string ZoneName;
        public string EncounterGroupKey;
        public int HP => (int)(CurrentHPWire / 256);
        public int MaxHP => (int)(MaxHPWire / 256);
        public void AddThreat(uint playerId, int amount)
        {
            if (_threatTable.ContainsKey(playerId))
                _threatTable[playerId] += amount;
            else
                _threatTable[playerId] = amount;
        }

        public void ClearTarget()
        {
            TargetId = 0;
            AlertSourceEntityId = 0;
            _threatTable.Clear();
        }
    }

    public enum MonsterState
    {
        Idle,       // Binary state 0 — wandering, fidgeting
        Chase,      // Moving toward target
        Combat,     // Binary state 5 — engaged in combat
        Attacking,  // Binary state 6 — actively executing attack
        Return,     // Binary state 7 — returning to spawn
        Dead        // Monster died
    }
}
