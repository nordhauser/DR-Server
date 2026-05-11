using UnityEngine;

namespace DungeonRunners.Data
{
    /// <summary>
    /// Represents an entity in the game world
    /// </summary>
    public class WorldEntity
    {
        public uint Id { get; set; }
        public float HP { get; set; }
        public float MP { get; set; }
        public int CollisionRadius { get; set; }
        public Vector3 WorldPosition { get; set; }
        public float Heading { get; set; }
        public uint WorldEntityFlags { get; set; }
        public byte WorldEntityInitFlags { get; set; }
        public string Label { get; set; }

        // Optional extras from Go
        public ushort Unk1Case { get; set; } = 0;
        public byte Unk2Case { get; set; } = 0;
        public uint Unk4Case { get; set; } = 0;

        public bool UseCustomAnimationSpeed { get; set; } = false;
        public float AnimationSpeed { get; set; } = 1.0f;

        public WorldEntity(uint id, string label, Vector3 pos, float heading)
        {
            Id = id;
            Label = label;
            WorldPosition = pos;
            Heading = heading;
            HP = 100;
            MP = 50;
            CollisionRadius = 32;
            WorldEntityFlags = 0x04; // Make visible
            WorldEntityInitFlags = 0;
        }
    }
}