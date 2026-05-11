using System.Collections.Generic;
using UnityEngine;
using DungeonRunners.Data;

namespace DungeonRunners.Managers
{
    /// <summary>
    /// Manages all game entities (players, NPCs, monsters)
    /// </summary>
    public class EntityManager : MonoBehaviour
    {
        private static EntityManager _instance;
        public static EntityManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("EntityManager");
                    _instance = go.AddComponent<EntityManager>();
                    DontDestroyOnLoad(go);
                }
                return _instance;
            }
        }

        private Dictionary<uint, GameEntity> _entities = new Dictionary<uint, GameEntity>();
        private uint _nextEntityId = 10000;
        private readonly object _lock = new object();

        void Awake()
        {
            if (_instance == null)
            {
                _instance = this;
                DontDestroyOnLoad(gameObject);
            }
            else if (_instance != this)
            {
                Destroy(gameObject);
            }
        }

        public GameEntity SpawnPlayerEntity(Character character)
        {
            lock (_lock)
            {
                uint entityId = _nextEntityId++;
                
                var entity = new GameEntity
                {
                    Id = entityId,
                    CharacterId = character.Id,
                    Name = character.Name,
                    Type = EntityType.Player,
                    Position = character.Position,
                    ZoneId = character.ZoneId,
                    WorldId = character.WorldId,
                    CurrentHP = character.CurrentHP,
                    MaxHP = character.MaxHP,
                    CurrentMP = character.CurrentMP,
                    MaxMP = character.MaxMP,
                    Level = character.Level
                };

                _entities[entityId] = entity;
                Debug.Log($"Spawned player entity: {entity.Name} (Entity ID: {entityId}) at {entity.Position}");
                return entity;
            }
        }

        public GameEntity GetEntity(uint entityId)
        {
            lock (_lock)
            {
                return _entities.ContainsKey(entityId) ? _entities[entityId] : null;
            }
        }

        public List<GameEntity> GetEntitiesInZone(int zoneId)
        {
            lock (_lock)
            {
                var result = new List<GameEntity>();
                foreach (var entity in _entities.Values)
                {
                    if (entity.ZoneId == zoneId)
                    {
                        result.Add(entity);
                    }
                }
                return result;
            }
        }

        public void UpdateEntityPosition(uint entityId, Vector3 position)
        {
            lock (_lock)
            {
                if (_entities.TryGetValue(entityId, out GameEntity entity))
                {
                    entity.Position = position;
                }
            }
        }

        public void RemoveEntity(uint entityId)
        {
            lock (_lock)
            {
                if (_entities.Remove(entityId))
                {
                    Debug.Log($"Removed entity: {entityId}");
                }
            }
        }

        public void ClearAllEntities()
        {
            lock (_lock)
            {
                _entities.Clear();
                Debug.Log("Cleared all entities");
            }
        }
    }

    [System.Serializable]
    public class GameEntity
    {
        public uint Id;
        public uint CharacterId;
        public string Name;
        public EntityType Type;
        public Vector3 Position;
        public int ZoneId;
        public int WorldId;
        public int CurrentHP;
        public int MaxHP;
        public int CurrentMP;
        public int MaxMP;
        public byte Level;
    }

    public enum EntityType
    {
        Player = 0,
        NPC = 1,
        Monster = 2,
        Item = 3
    }
}