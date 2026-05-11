using System;
using System.Collections.Generic;
using UnityEngine;
using DungeonRunners.Combat;

namespace DungeonRunners.Managers
{
    public class MazeGenerator
    {
        public const int TILE_SIZE = 400;

        public const int NORTH = 0;
        public const int EAST = 1;
        public const int SOUTH = 2;
        public const int WEST = 3;

        private static readonly int[] DX = { 0, 1, 0, -1 };
        private static readonly int[] DY = { 1, 0, -1, 0 };
        private static readonly int[] OPPOSITE = { SOUTH, WEST, NORTH, EAST };

        public int Width { get; private set; }
        public int Height { get; private set; }
        public uint Seed { get; private set; }
        public int Randomness { get; private set; }
        public int Sparseness { get; private set; }
        public int DeadEndRemovalChance { get; private set; }

        private HashSet<int>[][] _openings;
        private MersenneTwister _rng;

        public class MazeCell
        {
            public int GridX;
            public int GridY;
            public string Connections;
            public string TileType;
            public float WorldOriginX;
            public float WorldOriginY;
            public float WorldCenterX;
            public float WorldCenterY;
            public bool HasNorth => Connections.Contains("1n");
            public bool HasEast => Connections.Contains("1e");
            public bool HasSouth => Connections.Contains("1s");
            public bool HasWest => Connections.Contains("1w");
        }

        public MazeGenerator(int width, int height, uint seed,
                             int randomness = 90, int sparseness = 5,
                             int deadEndRemovalChance = 100)
        {
            Width = width;
            Height = height;
            Seed = seed;
            Randomness = randomness;
            Sparseness = sparseness;
            DeadEndRemovalChance = deadEndRemovalChance;
            _rng = new MersenneTwister(seed);

            _openings = new HashSet<int>[height][];
            for (int y = 0; y < height; y++)
            {
                _openings[y] = new HashSet<int>[width];
                for (int x = 0; x < width; x++)
                    _openings[y][x] = new HashSet<int>();
            }
        }

        private bool InBounds(int x, int y)
        {
            return x >= 0 && x < Width && y >= 0 && y < Height;
        }

        private void Connect(int x1, int y1, int dir)
        {
            int x2 = x1 + DX[dir];
            int y2 = y1 + DY[dir];
            if (InBounds(x2, y2))
            {
                _openings[y1][x1].Add(dir);
                _openings[y2][x2].Add(OPPOSITE[dir]);
            }
        }

        public float CenterOverrideX = float.NaN;
        public float CenterOverrideY = float.NaN;

        public List<MazeCell> Generate(string tileSetPrefix = "elmforest_tileset_")
        {
            GrowingTree();
            RemoveDeadEnds();
            ApplySparseness();
            return BuildResult(tileSetPrefix);
        }

        private void GrowingTree()
        {
            bool[][] visited = new bool[Height][];
            for (int y = 0; y < Height; y++)
                visited[y] = new bool[Width];

            int startX = Width / 2;
            int startY = Height - 1;
            visited[startY][startX] = true;
            var activeList = new List<(int x, int y)> { (startX, startY) };

            while (activeList.Count > 0)
            {
                int idx;
                if (NextInt(1, 101) <= Randomness)
                    idx = NextInt(0, activeList.Count);
                else
                    idx = activeList.Count - 1;

                var (cx, cy) = activeList[idx];

                var neighbors = new List<(int dir, int nx, int ny)>();
                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + DX[d];
                    int ny = cy + DY[d];
                    if (InBounds(nx, ny) && !visited[ny][nx])
                        neighbors.Add((d, nx, ny));
                }

                if (neighbors.Count > 0)
                {
                    var (dir, nx, ny) = neighbors[NextInt(0, neighbors.Count)];
                    Connect(cx, cy, dir);
                    visited[ny][nx] = true;
                    activeList.Add((nx, ny));
                }
                else
                {
                    activeList.RemoveAt(idx);
                }
            }
        }

        private void RemoveDeadEnds()
        {
            bool changed = true;
            while (changed)
            {
                changed = false;
                for (int y = 0; y < Height; y++)
                {
                    for (int x = 0; x < Width; x++)
                    {
                        if (_openings[y][x].Count == 1)
                        {
                            if (NextInt(1, 101) <= DeadEndRemovalChance)
                            {
                                var unconnected = new List<int>();
                                for (int d = 0; d < 4; d++)
                                {
                                    if (!_openings[y][x].Contains(d) &&
                                        InBounds(x + DX[d], y + DY[d]))
                                    {
                                        unconnected.Add(d);
                                    }
                                }

                                if (unconnected.Count > 0)
                                {
                                    int dir = unconnected[NextInt(0, unconnected.Count)];
                                    Connect(x, y, dir);
                                    changed = true;
                                }
                            }
                        }
                    }
                }
            }
        }

        private void ApplySparseness()
        {
            var walls = new List<(int x, int y, int dir)>();
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    if (!_openings[y][x].Contains(NORTH) && InBounds(x, y + 1))
                        walls.Add((x, y, NORTH));
                    if (!_openings[y][x].Contains(EAST) && InBounds(x + 1, y))
                        walls.Add((x, y, EAST));
                }
            }

            int removeCount = walls.Count * Sparseness / 100;

            for (int i = walls.Count - 1; i > 0; i--)
            {
                int j = NextInt(0, i + 1);
                var tmp = walls[i];
                walls[i] = walls[j];
                walls[j] = tmp;
            }

            for (int i = 0; i < removeCount && i < walls.Count; i++)
            {
                Connect(walls[i].x, walls[i].y, walls[i].dir);
            }
        }

        private List<MazeCell> BuildResult(string tileSetPrefix)
        {
            float cX = float.IsNaN(CenterOverrideX) ? 0f : CenterOverrideX;
            float cY = float.IsNaN(CenterOverrideY) ? 0f : CenterOverrideY;
            float originX = cX - (Width * TILE_SIZE) / 2f;
            float originY = cY - (Height * TILE_SIZE) / 2f;

            var cells = new List<MazeCell>();

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    string conns = "";
                    if (_openings[y][x].Contains(NORTH)) conns += "1n";
                    if (_openings[y][x].Contains(EAST)) conns += "1e";
                    if (_openings[y][x].Contains(SOUTH)) conns += "1s";
                    if (_openings[y][x].Contains(WEST)) conns += "1w";

                    if (string.IsNullOrEmpty(conns))
                        conns = "0n";

                    float cellOX = originX + x * TILE_SIZE;
                    float cellOY = originY + y * TILE_SIZE;

                    cells.Add(new MazeCell
                    {
                        GridX = x,
                        GridY = y,
                        Connections = conns,
                        TileType = $"{tileSetPrefix}{conns}_a",
                        WorldOriginX = cellOX,
                        WorldOriginY = cellOY,
                        WorldCenterX = cellOX + TILE_SIZE / 2f,
                        WorldCenterY = cellOY + TILE_SIZE / 2f,
                    });
                }
            }

            return cells;
        }

        public string GetConnections(int gx, int gy)
        {
            if (!InBounds(gx, gy)) return null;
            string conns = "";
            if (_openings[gy][gx].Contains(NORTH)) conns += "1n";
            if (_openings[gy][gx].Contains(EAST)) conns += "1e";
            if (_openings[gy][gx].Contains(SOUTH)) conns += "1s";
            if (_openings[gy][gx].Contains(WEST)) conns += "1w";
            return conns;
        }

        public int NextInt(int minInclusive, int maxExclusive)
        {
            if (maxExclusive <= minInclusive)
                return minInclusive;
            return (int)_rng.Generate((uint)minInclusive, (uint)(maxExclusive - 1));
        }

        public float NextFloat(float minInclusive, float maxExclusive)
        {
            if (maxExclusive <= minInclusive)
                return minInclusive;
            uint raw = _rng.Generate() >> 8;
            float t = raw / 16777216f;
            return minInclusive + (maxExclusive - minInclusive) * t;
        }

        public void PrintMaze()
        {
            for (int y = Height - 1; y >= 0; y--)
            {
                string top = "";
                string mid = "";
                for (int x = 0; x < Width; x++)
                {
                    bool hasN = _openings[y][x].Contains(NORTH);
                    bool hasW = _openings[y][x].Contains(WEST);
                    top += "+" + (hasN ? "   " : "---");
                    mid += (hasW ? " " : "|") + $"({x},{y})";
                }
                top += "+";
                mid += "|";
                Debug.LogError(top);
                Debug.LogError(mid);
            }
            string bottom = "";
            for (int x = 0; x < Width; x++)
                bottom += "+---";
            bottom += "+";
            Debug.LogError(bottom);
        }
    }
}
