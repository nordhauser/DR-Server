using System;
using System.Collections.Generic;
using UnityEngine;
using DungeonRunners.Utilities;

namespace DungeonRunners.Data
{
    /// <summary>
    /// CORRECT structure: Player has 1 child (Avatar) which contains the 9 objects
    /// </summary>
    public static class PlayerSerializerCorrect
    {
        public static byte[] CreatePlayerData(Character character)
        {
            var writer = new LEWriter();

            // Write packet header
            writer.WriteByte(0x04);
            writer.WriteByte(0x03);
            writer.WriteByte(0x02);
            writer.WriteByte(0x0A);
            writer.WriteUInt32(0x2D000000);

            // Write Player hash
            writer.WriteUInt32(0x14FA6292); // Player hash

            // Write Player ID
            writer.WriteUInt32(character.Id);

            // Write Player name
            writer.WriteCString(character.Name);

            // CRITICAL: Player has 1 child (the Avatar container)
            writer.WriteUInt32(1); // ONE child, not 9!

            // Write the Avatar child object that contains everything
            WriteAvatarContainer(writer, character);

            // Write Player GCClass hash
            writer.WriteUInt32(0x14FA6292); // Player

            // Write Player properties (Avatar Name property)
            writer.WriteUInt32(0xB1E064F2); // "Avatar Name" hash (from working packet: 2d 64 e0 b1 f2)
            writer.WriteCString(""); // Empty avatar name

            // End marker
            writer.WriteUInt32(0);

            return writer.ToArray();
        }

        private static void WriteAvatarContainer(LEWriter writer, Character character)
        {
            // This is the container object that holds all 9 children
            // From working packet at 0x20: 2d 64 e0 b1 f2

            writer.WriteByte(0x2D); // DFC version

            // Write container hash (from working packet)
            writer.WriteUInt32(0xF2B1E064); // This is the container class hash

            // Write container ID
            writer.WriteUInt32(0); // ID = 0

            // Write container name (empty)
            // writer.WriteCString("");
            writer.WriteCString("Avatar Name");

            // Write number of children (8 from working packet at 0x30: 08 00 00 00)
            writer.WriteUInt32(8); // 8 children in the container

            // Write the 8 child objects
            WriteEquipment(writer);
            WriteSkills(writer);
            WriteUnitContainer(writer);
            WriteBaseInventory(writer);
            WriteBankInventory(writer);
            WriteTradeInventory(writer);
            WriteBehaviour(writer);
            WriteDialogManager(writer);
            // Note: QuestManager might be separate or part of another structure

            // Write container GCClass hash
            writer.WriteUInt32(0xF2B1E064);

            // End marker
            writer.WriteUInt32(0);
        }

        private static void WriteEquipment(LEWriter writer)
        {
            writer.WriteByte(0x2D);
            writer.WriteUInt32(0x5BB7BF9D); // Equipment hash
            writer.WriteUInt32(0x5BB7BF9D); // ID
            writer.WriteCString("EllieEquipment");
            writer.WriteUInt32(5); // 5 equipment slots

            // Write 5 equipment slot objects (all empty)
            for (int i = 0; i < 5; i++)
            {
                writer.WriteByte(0x2D);
                writer.WriteUInt32(0x0F1AAAA6); // Empty slot hash
                writer.WriteUInt32(0); // ID
                writer.WriteCString("");
                writer.WriteUInt32(0); // No children
                writer.WriteUInt32(0x0F1AAAA6); // GCClass
                writer.WriteUInt32(0); // End marker
            }

            writer.WriteUInt32(0x5BB7BF9D); // GCClass
            writer.WriteUInt32(0); // End marker
        }

        private static void WriteSkills(LEWriter writer)
        {
            writer.WriteByte(0x2D);
            writer.WriteUInt32(0x1BEBF097); // Skills hash
            writer.WriteUInt32(0x1BEBF097); // ID
            writer.WriteCString("EllieSkills");
            writer.WriteUInt32(0); // No children
            writer.WriteUInt32(0x1BEBF097); // GCClass
            writer.WriteUInt32(0); // End marker
        }

        private static void WriteUnitContainer(LEWriter writer)
        {
            writer.WriteByte(0x2D);
            writer.WriteUInt32(0xE9AB2A48); // UnitContainer hash
            writer.WriteUInt32(0xE9AB2A48); // ID
            writer.WriteCString("EllieUnitContainer");
            writer.WriteUInt32(0); // No children
            writer.WriteUInt32(0xE9AB2A48); // GCClass
            writer.WriteUInt32(0); // End marker
        }

        private static void WriteBaseInventory(LEWriter writer)
        {
            writer.WriteByte(0x2D);
            writer.WriteUInt32(0xAC692AF3); // Inventory hash
            writer.WriteUInt32(0xAC692AF3); // ID
            writer.WriteCString("EllieBaseInventory");
            writer.WriteUInt32(0); // No children
            writer.WriteUInt32(0xAC692AF3); // GCClass
            writer.WriteUInt32(0); // End marker
        }

        private static void WriteBankInventory(LEWriter writer)
        {
            writer.WriteByte(0x2D);
            writer.WriteUInt32(0xAC692AF3); // Inventory hash
            writer.WriteUInt32(0xAC692AF3); // ID
            writer.WriteCString("EllieBankInventory");
            writer.WriteUInt32(0); // No children
            writer.WriteUInt32(0xAC692AF3); // GCClass
            writer.WriteUInt32(0); // End marker
        }

        private static void WriteTradeInventory(LEWriter writer)
        {
            writer.WriteByte(0x2D);
            writer.WriteUInt32(0xAC692AF3); // Inventory hash
            writer.WriteUInt32(0xAC692AF3); // ID
            writer.WriteCString("EllieTradeInventory");
            writer.WriteUInt32(0); // No children
            writer.WriteUInt32(0xAC692AF3); // GCClass
            writer.WriteUInt32(0); // End marker
        }

        private static void WriteBehaviour(LEWriter writer)
        {
            writer.WriteByte(0x2D);
            writer.WriteUInt32(0xF038A42A); // Behaviour hash
            writer.WriteUInt32(0xF038A42A); // ID
            writer.WriteCString("EllieBehaviour");
            writer.WriteUInt32(0); // No children
            writer.WriteUInt32(0xF038A42A); // GCClass
            writer.WriteUInt32(0); // End marker
        }

        private static void WriteDialogManager(LEWriter writer)
        {
            writer.WriteByte(0x2D);
            writer.WriteUInt32(0xFFC1A4D0); // DialogManager hash
            writer.WriteUInt32(0xFFC1A4D0); // ID
            writer.WriteCString("EllieDialogManager");
            writer.WriteUInt32(0); // No children
            writer.WriteUInt32(0xFFC1A4D0); // GCClass
            writer.WriteUInt32(0); // End marker
        }
    }
}