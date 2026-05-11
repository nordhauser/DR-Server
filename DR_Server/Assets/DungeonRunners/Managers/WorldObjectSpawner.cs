using System;
using System.Collections.Generic;
using UnityEngine;

namespace DungeonRunners.Managers
{
    // ═══════════════════════════════════════════════════════════════════════════
    // WORLD OBJECT SPAWNER — Barrels + Treasure Chests in dungeon zones
    //
    // Barrels: Spawned as one-hit creatures via CombatManager.SpawnMonster().
    //          When killed, ProcessMonsterKill fires → LootManager.GenerateBarrelLoot().
    //
    // Chests:  Spawned as NCI entities using SAME packet pattern as checkpoints.
    //          When clicked, HandleChestActivation fires → LootManager.GenerateChestLoot().
    //          Uses same 0x01 create + 0x02 init + 0x35 activation as portals/checkpoints.
    //
    // Binary: World::attachObjects spawns StaticObjectGeneratorTable (barrels)
    //         + WorldEntityGeneratorTable (chests/NCIs) per zone tile.
    // ═══════════════════════════════════════════════════════════════════════════

    public static class WorldObjectSpawner
    {
        // ── Barrel GC types (added to creatures table via add_barrels.sql) ──
        private static readonly string[] BarrelTypes =
        {
            "world.objects.barrel.breakable",
            "world.objects.barrel.breakable.02",
            "world.objects.barrel.breakable.03",
        };
        private const string CrateType = "world.objects.crate.breakable";

        // ── Chest GC types (NCI entities, match original binary GC paths) ──
        private static readonly string[] SmallChestTypes =
        {
            "terrain.interactives.loot.Chest_Sm_01",
            "terrain.interactives.loot.Chest_Sm_02",
            "terrain.interactives.loot.Chest_Sm_03",
        };
        private static readonly string[] MediumChestTypes =
        {
            "terrain.interactives.loot.Chest_Md_01",
            "terrain.interactives.loot.Chest_Md_02",
            "terrain.interactives.loot.Chest_Md_03",
        };
        private const string LargeChestType = "terrain.interactives.loot.Chest_Lg_01";

        // ── Placement offsets within maze tiles ──
        private static readonly float[][] ObjectOffsets =
        {
            new[] { -40f, -30f }, new[] {  35f, -25f },
            new[] { -30f,  40f }, new[] {  45f,  35f },
            new[] { -50f,   0f }, new[] {   0f, -50f },
            new[] {  25f,  50f }, new[] { -45f,  20f },
        };

        // ═══════════════════════════════════════════════════════════════
        // BARREL SPAWNS — returned as DungeonSpawnData for SpawnMonster
        // ═══════════════════════════════════════════════════════════════

        public static List<DatabaseLoader.DungeonSpawnData> GenerateBarrels(
            string zoneName, uint seed = 0xBEEFBEEF)
        {
            var spawns = new List<DatabaseLoader.DungeonSpawnData>();
            if (!DungeonMazeSpawner.IsProceduralZone(zoneName)) return spawns;

            var rng = new System.Random((int)seed + zoneName.GetHashCode());
            var cells = GetMazeCells(zoneName, seed);

            foreach (var cell in cells)
            {
                if (cell.isEntry) continue;

                // 40% of cells get 1-2 barrels
                if (rng.NextDouble() < 0.40)
                {
                    int count = rng.Next(1, 3);
                    for (int i = 0; i < count; i++)
                    {
                        var off = ObjectOffsets[rng.Next(ObjectOffsets.Length)];
                        float px = cell.cx + off[0] + (float)(rng.NextDouble() * 10 - 5);
                        float py = cell.cy + off[1] + (float)(rng.NextDouble() * 10 - 5);
                        spawns.Add(new DatabaseLoader.DungeonSpawnData
                        {
                            zoneName = zoneName,
                            gcType = BarrelTypes[rng.Next(BarrelTypes.Length)],
                            posX = px,
                            posY = py,
                            posZ = Core.PathMapManager.Instance.GetHeight(zoneName, px, py, 10f),
                            heading = rng.Next(360)
                        });
                    }
                }

                // 15% of cells get a crate
                if (rng.NextDouble() < 0.15)
                {
                    var off = ObjectOffsets[rng.Next(ObjectOffsets.Length)];
                    float px = cell.cx + off[0];
                    float py = cell.cy + off[1];
                    spawns.Add(new DatabaseLoader.DungeonSpawnData
                    {
                        zoneName = zoneName,
                        gcType = CrateType,
                        posX = px,
                        posY = py,
                        posZ = Core.PathMapManager.Instance.GetHeight(zoneName, px, py, 10f),
                        heading = rng.Next(360)
                    });
                }
            }

            Debug.LogError($"[WorldObjects] {zoneName}: {spawns.Count} barrels/crates");
            return spawns;
        }

        // ═══════════════════════════════════════════════════════════════
        // CHEST SPAWNS — returned as ChestSpawnData for NCI entity packets
        // Spawned using SAME pattern as checkpoints (0x01 + 0x02)
        // ═══════════════════════════════════════════════════════════════

        public static List<ChestSpawnData> GenerateChests(
            string zoneName, uint seed = 0xBEEFBEEF)
        {
            var chests = new List<ChestSpawnData>();
            if (!DungeonMazeSpawner.IsProceduralZone(zoneName)) return chests;

            var rng = new System.Random((int)(seed * 7) + zoneName.GetHashCode());
            var cells = GetMazeCells(zoneName, seed);

            int cellIndex = 0;
            foreach (var cell in cells)
            {
                if (cell.isEntry) { cellIndex++; continue; }

                // Small chest: ~20% of cells (1 per cell max)
                if (rng.NextDouble() < 0.20)
                {
                    var off = ObjectOffsets[(cellIndex + 3) % ObjectOffsets.Length];
                    float px = cell.cx + off[0];
                    float py = cell.cy + off[1];
                    SnapAndGetHeight(zoneName, ref px, ref py, out float pz);
                    chests.Add(new ChestSpawnData
                    {
                        GCType = SmallChestTypes[rng.Next(SmallChestTypes.Length)],
                        Label = "Treasure Chest",
                        PosX = px,
                        PosY = py,
                        PosZ = pz,
                        Heading = rng.Next(360),
                        ItemGenerator = "TreasureChestSmallIG",
                        ItemCount = 1,
                    });
                }

                // Medium chest: ~5% of cells (rarer, better loot)
                if (rng.NextDouble() < 0.05)
                {
                    var off = ObjectOffsets[(cellIndex + 5) % ObjectOffsets.Length];
                    float px = cell.cx + off[0];
                    float py = cell.cy + off[1];
                    SnapAndGetHeight(zoneName, ref px, ref py, out float pz);
                    chests.Add(new ChestSpawnData
                    {
                        GCType = MediumChestTypes[rng.Next(MediumChestTypes.Length)],
                        Label = "Large Treasure Chest",
                        PosX = px,
                        PosY = py,
                        PosZ = pz,
                        Heading = rng.Next(360),
                        ItemGenerator = "TreasureChestMediumIG",
                        ItemCount = 2,
                    });
                }

                cellIndex++;
            }

            // One large chest per zone (boss-quality, always in last cell)
            if (cells.Count > 2)
            {
                var lastCell = cells[cells.Count - 1];
                float lx = lastCell.cx;
                float ly = lastCell.cy + 30f;
                SnapAndGetHeight(zoneName, ref lx, ref ly, out float lz);
                chests.Add(new ChestSpawnData
                {
                    GCType = LargeChestType,
                    Label = "Grand Treasure Chest",
                    PosX = lx,
                    PosY = ly,
                    PosZ = lz,
                    Heading = 0,
                    ItemGenerator = "TreasureChestLargeIG",
                    ItemCount = 3,
                });
            }

            Debug.LogError($"[WorldObjects] {zoneName}: {chests.Count} treasure chests");
            return chests;
        }

        /// <summary>
        /// Snap position to nearest walkable pathmap node and get correct Z height.
        /// Same approach as DungeonMazeSpawner.FindWalkableSpot for mobs.
        /// </summary>
        private static void SnapAndGetHeight(string zoneName, ref float x, ref float y, out float z)
        {
            var (safeX, safeY, found) = DungeonMazeSpawner.FindWalkableSpot(zoneName, x, y);
            if (found)
            {
                x = safeX;
                y = safeY;
            }
            z = Core.PathMapManager.Instance.GetHeight(zoneName, x, y, 50f) + 3f;
        }

        // ═══════════════════════════════════════════════════════════════
        // HELPERS
        // ═══════════════════════════════════════════════════════════════

        public static bool IsDestroyableObject(string gcType)
        {
            if (string.IsNullOrEmpty(gcType)) return false;
            return gcType.StartsWith("world.objects.barrel", StringComparison.OrdinalIgnoreCase) ||
                   gcType.StartsWith("world.objects.crate", StringComparison.OrdinalIgnoreCase) ||
                   gcType.StartsWith("terrain.misc.interactives", StringComparison.OrdinalIgnoreCase);
        }

        private struct CellPos { public float cx, cy; public bool isEntry; }

        private static List<CellPos> GetMazeCells(string zoneName, uint seed)
        {
            if (!DungeonMazeSpawner.TryGetMazeDimensions(zoneName,
                out int width, out int height, out int entryX, out int entryY,
                out int randomness, out int sparseness, out int deadEndRemoval))
                return new List<CellPos>();

            var maze = new MazeGenerator(width, height, seed,
                randomness, sparseness, deadEndRemoval);
            var cells = maze.Generate();
            var result = new List<CellPos>();
            foreach (var c in cells)
                result.Add(new CellPos
                {
                    cx = c.WorldCenterX,
                    cy = c.WorldCenterY,
                    isEntry = (c.GridX == entryX && c.GridY == entryY)
                });
            return result;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CHEST SPAWN DATA — position + loot info for NCI entity spawning
    // ═══════════════════════════════════════════════════════════════════════════

    public class ChestSpawnData
    {
        public string GCType;          // terrain.interactives.loot.Chest_Sm_01
        public string Label;           // Display name
        public float PosX, PosY, PosZ;
        public float Heading;
        public string ItemGenerator;   // TreasureChestIG, TreasureChestSmallIG
        public int ItemCount;          // How many items to generate
    }
}