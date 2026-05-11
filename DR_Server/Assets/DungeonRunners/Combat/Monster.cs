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
        public bool ClientHPDirty;
        public float ClientHPDirtySince;
        public string ClientHPDirtyReason;
        public uint ClientHPDirtyStartWire;
        public float ClientHPDirtyAllowSameConfirmAt;
        public int SuppressedMonsterHPSyncPackets;
        public float LastClientHPReportTime;
        public uint LastClientHPReportWire;
        public uint MaxManaWire;
        public uint CurrentManaWire;
        public int BaseDamage;
        public float AttackRating = 1.0f;
        public float DamageMod = 1.0f;
        public float DamageVolatility = 0.5f;
        public float WeaponDamage = 1.0f;
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

        public float AggroRange = 50f;
        public float PerceptionRange = 0f;
        public float ShoutRange = 0f;
        public float LeashRange = 30f;
        public float AttackRange = 2.5f;
        public float ClientSyncTolerance = 10f;
        public float CollisionRadius = 5f;
        public float AttackCooldown = 1.5f;
        public float AttackLeadDelay = 0.75f;
        public float MoveSpeed = 5f;
        public float WalkSpeed = 25f;
        public float WanderRange = 0f;
        public uint LastStateCounter;

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
        public byte AttackSessionId;
        public byte AttackAnimationIndex;
        public uint AttackUseRaw;
        public bool AttackClientVisible;
        public float AttackCommitTargetX;
        public float AttackCommitTargetY;
        public uint CombatContactTargetId;
        public float CombatContactUntil;
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

        // Server-side Think/DoAttack/ComputeDamage REMOVED.
        // Client is authoritative on combat — it runs MeleeWeapon::update (0x591980)
        // every frame, computes all damage locally, and sends type 9 for aggro.
        // HP validation patched out of client (0x5DD9E4: je -> jmp).

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
