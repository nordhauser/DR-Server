using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonRunners.Data
{
    /// <summary>
    /// Complete character data structure for Dungeon Runners
    /// </summary>
    [Serializable]
    public class Character
    {
        public uint Id;
        public string Name;
        public uint AccountId;
        public byte Level;
        public uint Experience;
        public uint Gold;
        public Vector3 Position;
        public int ZoneId;
        public int WorldId;

        // Stats
        public int CurrentHP;
        public int MaxHP;
        public int CurrentMP;
        public int MaxMP;

        // Equipment (10 slots)
        public Equipment Equipment;

        // Skills (9 default skills)
        public List<Skill> Skills;

        // Inventories
        public Inventory BaseInventory;
        public Inventory BankInventory;
        public Inventory TradeInventory;

        // Appearance
        public byte Gender;
        public byte HairStyle;
        public byte HairColor;
        public byte FaceStyle;
        public byte SkinColor;

        public Character()
        {
            Equipment = new Equipment();
            Skills = new List<Skill>();
            BaseInventory = new Inventory(30);
            BankInventory = new Inventory(50);
            TradeInventory = new Inventory(10);

            // Initialize default skills
            InitializeDefaultSkills();
        }

        private void InitializeDefaultSkills()
        {
            Skills.Add(new Skill { Id = 1, Level = 1, Name = "Basic Attack" });
            Skills.Add(new Skill { Id = 2, Level = 1, Name = "Heal" });
            Skills.Add(new Skill { Id = 3, Level = 1, Name = "Fireball" });
            Skills.Add(new Skill { Id = 4, Level = 1, Name = "Ice Bolt" });
            Skills.Add(new Skill { Id = 5, Level = 1, Name = "Lightning" });
            Skills.Add(new Skill { Id = 6, Level = 1, Name = "Shield" });
            Skills.Add(new Skill { Id = 7, Level = 1, Name = "Buff" });
            Skills.Add(new Skill { Id = 8, Level = 1, Name = "Debuff" });
            Skills.Add(new Skill { Id = 9, Level = 1, Name = "Ultimate" });
        }
    }

    [Serializable]
    public class Equipment
    {
        public Item Weapon;
        public Item Armor;
        public Item Helmet;
        public Item Gloves;
        public Item Boots;
        // NEW SLOTS
        public Item Shoulders;
        public Item Shield;
        public Item Ring1;
        public Item Ring2;
        public Item Amulet;

        public Equipment()
        {
            Weapon = null;
            Armor = null;
            Helmet = null;
            Gloves = null;
            Boots = null;
            Shoulders = null;
            Shield = null;
            Ring1 = null;
            Ring2 = null;
            Amulet = null;
        }
    }

    [Serializable]
    public class Item
    {
        public uint Id;
        public uint TemplateId;
        public string Name;
        public byte Quantity;
        public byte Slot;

        public Item(uint id, uint templateId, string name, byte quantity = 1)
        {
            Id = id;
            TemplateId = templateId;
            Name = name;
            Quantity = quantity;
        }
    }

    [Serializable]
    public class Skill
    {
        public uint Id;
        public string Name;
        public byte Level;
        public uint Experience;

        public Skill()
        {
            Level = 1;
            Experience = 0;
        }
    }

    [Serializable]
    public class Inventory
    {
        public int MaxSlots;
        public List<Item> Items;

        public Inventory(int maxSlots)
        {
            MaxSlots = maxSlots;
            Items = new List<Item>();
        }

        public bool AddItem(Item item)
        {
            if (Items.Count >= MaxSlots)
                return false;

            Items.Add(item);
            return true;
        }

        public bool RemoveItem(uint itemId)
        {
            Item item = Items.Find(i => i.Id == itemId);
            if (item != null)
            {
                Items.Remove(item);
                return true;
            }
            return false;
        }
    }
}