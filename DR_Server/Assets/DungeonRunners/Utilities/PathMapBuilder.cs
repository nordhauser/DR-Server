using System;
using System.Collections.Generic;
using DungeonRunners.Core;
using DungeonRunners.Managers;
using UnityEngine;

namespace DungeonRunners.Utilities
{
    /// <summary>
    /// Builds a per-instance <see cref="PathMap"/> from a list of <see cref="MazeGenerator.MazeCell"/>s
    /// by composing the placed tiles' collision geometry. Phase 3 of Option 1-full.
    ///
    /// Pipeline per cell:
    /// <list type="number">
    ///   <item>Resolve the cell's <c>TileType</c> to a <c>.tile</c> file via <see cref="TileCobjResolver.ResolveTilePath"/>.</item>
    ///   <item>Parse the <c>.tile</c> into placements (each = subasset path + Position + Heading) via <see cref="TileLayoutLoader.Load"/>.</item>
    ///   <item>For each placement that resolves to a <c>.cobj</c>, parse the cobj heightmap, transform each blocked cell into world space (apply placement Position + Heading + cell.WorldOrigin), and mark the corresponding PathMap nodes blocked.</item>
    /// </list>
    /// The remaining nodes in the maze bounding box are filled as walkable.
    ///
    /// v1: only uses sub-shape 1 (heightmap) of <see cref="CobjData"/>; cells whose height exceeds
    /// <see cref="WallHeightThreshold"/> raw units are treated as blocking. Sub-shape 2 (multi-floor
    /// bbox stacks) is ignored — would only matter for bridges/archways. Heading uses Fixed32Math
    /// SIN/COS LUT for client parity.
    /// </summary>
    public static class PathMapBuilder
    {
        /// <summary>World-units per PathMap node (matches <see cref="PathMap"/> internal TILE_SIZE).</summary>
        public const float NodeResolution = 10f;

        /// <summary>Maze cell side length in world units (matches <see cref="MazeGenerator.TILE_SIZE"/>).</summary>
        public const float MazeTileSize = 400f;

        /// <summary>Cobj heightmap entries above this value are treated as blocking (walls).</summary>
        public const int WallHeightThreshold = 30;

        public static PathMap Build(string zoneName, IReadOnlyList<MazeGenerator.MazeCell> cells)
        {
            if (cells == null || cells.Count == 0)
            {
                Debug.LogError($"[PATHMAP-BUILD] zone='{zoneName}' SKIP — empty cell list");
                return null;
            }

            ComputeWorldBounds(cells, out float minX, out float maxX, out float minY, out float maxY);
            var pathMap = PathMap.CreateEmpty(zoneName, minX, maxX, minY, maxY);

            int gridW = (int)Math.Ceiling((maxX - minX) / NodeResolution) + 1;
            int gridH = (int)Math.Ceiling((maxY - minY) / NodeResolution) + 1;
            var blocked = new bool[gridW, gridH];
            var inMazeFootprint = new bool[gridW, gridH];

            int tilesProcessed = 0;
            int tilesSkippedNoFile = 0;
            int placementsProcessed = 0;
            int placementsSkippedNoCobj = 0;
            int blockedCellsTotal = 0;
            List<string> missingTileTypes = null;

            foreach (var cell in cells)
            {
                MarkMazeFootprint(cell, inMazeFootprint, gridW, gridH, minX, minY);

                string tilePath = TileCobjResolver.ResolveTilePath(cell.TileType);
                if (tilePath == null)
                {
                    tilesSkippedNoFile++;
                    (missingTileTypes ??= new List<string>()).Add(cell.TileType);
                    continue;
                }

                TileLayout layout;
                try { layout = TileLayoutLoader.Load(tilePath); }
                catch (Exception e)
                {
                    Debug.LogError($"[PATHMAP-BUILD] tile parse error: {cell.TileType} — {e.Message}");
                    continue;
                }

                tilesProcessed++;
                foreach (var placement in layout.Placements)
                {
                    string cobjPath = TileCobjResolver.ResolveCobjPath(placement.ExtendsPath);
                    if (cobjPath == null) { placementsSkippedNoCobj++; continue; }

                    CobjData cobj;
                    try { cobj = CobjParser.ParseFile(cobjPath); }
                    catch (Exception)
                    {
                        placementsSkippedNoCobj++;
                        continue;
                    }

                    placementsProcessed++;
                    int blockedHere = ApplyCobjToBlockedGrid(
                        cobj, placement, cell,
                        blocked, gridW, gridH, minX, minY);
                    blockedCellsTotal += blockedHere;
                }
            }

            int walkable = 0;
            int blockedCount = 0;
            for (int gx = 0; gx < gridW; gx++)
            {
                for (int gy = 0; gy < gridH; gy++)
                {
                    if (!inMazeFootprint[gx, gy]) continue;

                    bool isBlocked = blocked[gx, gy];
                    var node = new PathNode
                    {
                        GridX = gx,
                        GridY = gy,
                        WorldX = minX + gx * NodeResolution,
                        WorldY = minY + gy * NodeResolution,
                        Height = 0f,
                        // Allow all 8 directions for walkable nodes. The client's PathMap
                        // bakes connection flags from level data; v1 lets the A* expander
                        // rely on per-neighbor walkability instead.
                        ConnectionFlags = isBlocked ? (byte)0x00 : (byte)0xFF,
                        SolidFlag = isBlocked ? (byte)0xFE : (byte)0x00,
                    };
                    pathMap.SetNode(node);
                    if (isBlocked) blockedCount++; else walkable++;
                }
            }

            Debug.LogError(
                $"[PATHMAP-BUILD] zone='{zoneName}' cells={cells.Count} tiles={tilesProcessed}/{cells.Count} ({tilesSkippedNoFile} no-file) " +
                $"placements={placementsProcessed} ({placementsSkippedNoCobj} no-cobj) " +
                $"nodes={pathMap.NodeCount} (walkable={walkable} blocked={blockedCount}) " +
                $"bounds=({minX:F0},{minY:F0})→({maxX:F0},{maxY:F0}) blockedCobjCells={blockedCellsTotal}");

            if (missingTileTypes != null && missingTileTypes.Count > 0)
                Debug.LogError($"[PATHMAP-BUILD] missing tile files for zone='{zoneName}': {string.Join(", ", missingTileTypes)}");

            return pathMap;
        }

        private static void ComputeWorldBounds(
            IReadOnlyList<MazeGenerator.MazeCell> cells,
            out float minX, out float maxX, out float minY, out float maxY)
        {
            minX = float.PositiveInfinity; minY = float.PositiveInfinity;
            maxX = float.NegativeInfinity; maxY = float.NegativeInfinity;
            foreach (var c in cells)
            {
                if (c.WorldOriginX < minX) minX = c.WorldOriginX;
                if (c.WorldOriginY < minY) minY = c.WorldOriginY;
                float xR = c.WorldOriginX + MazeTileSize;
                float yT = c.WorldOriginY + MazeTileSize;
                if (xR > maxX) maxX = xR;
                if (yT > maxY) maxY = yT;
            }
        }

        private static void MarkMazeFootprint(
            MazeGenerator.MazeCell cell, bool[,] inMaze,
            int gridW, int gridH, float minX, float minY)
        {
            int gx0 = (int)Math.Floor((cell.WorldOriginX - minX) / NodeResolution);
            int gy0 = (int)Math.Floor((cell.WorldOriginY - minY) / NodeResolution);
            int gx1 = (int)Math.Ceiling((cell.WorldOriginX + MazeTileSize - minX) / NodeResolution);
            int gy1 = (int)Math.Ceiling((cell.WorldOriginY + MazeTileSize - minY) / NodeResolution);
            for (int gx = Math.Max(0, gx0); gx < Math.Min(gridW, gx1); gx++)
            {
                for (int gy = Math.Max(0, gy0); gy < Math.Min(gridH, gy1); gy++)
                {
                    inMaze[gx, gy] = true;
                }
            }
        }

        private static int ApplyCobjToBlockedGrid(
            CobjData cobj, TilePlacement placement, MazeGenerator.MazeCell cell,
            bool[,] blocked, int gridW, int gridH, float minX, float minY)
        {
            if (cobj.Width1 <= 0 || cobj.Height1 <= 0) return 0;

            int cs = cobj.CellSize1;

            float cosT = Mathf.Cos(placement.Heading * Mathf.Deg2Rad);
            float sinT = Mathf.Sin(placement.Heading * Mathf.Deg2Rad);

            int blockedHere = 0;
            for (int cy = 0; cy < cobj.Height1; cy++)
            {
                for (int cx = 0; cx < cobj.Width1; cx++)
                {
                    ushort h = cobj.Heightmap[cy * cobj.Width1 + cx];
                    if (h <= WallHeightThreshold) continue;

                    float lx = cobj.OriginX1 + (cx + 0.5f) * cs;
                    float ly = cobj.OriginY1 + (cy + 0.5f) * cs;

                    float rx = lx * cosT - ly * sinT;
                    float ry = lx * sinT + ly * cosT;

                    float wx = cell.WorldOriginX + placement.X + rx;
                    float wy = cell.WorldOriginY + placement.Y + ry;

                    int gxNode = (int)Math.Round((wx - minX) / NodeResolution);
                    int gyNode = (int)Math.Round((wy - minY) / NodeResolution);
                    if (gxNode < 0 || gxNode >= gridW || gyNode < 0 || gyNode >= gridH) continue;

                    if (!blocked[gxNode, gyNode])
                    {
                        blocked[gxNode, gyNode] = true;
                        blockedHere++;
                    }
                }
            }
            return blockedHere;
        }
    }
}
