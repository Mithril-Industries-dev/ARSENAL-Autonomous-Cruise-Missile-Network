using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using UnityEngine;

namespace Arsenal
{
    /// <summary>
    /// Custom pathfinding grid for DART drones.
    /// DARTs can fly over buildings, roofs, and water but NOT mountains.
    /// </summary>
    public class FlightPathGrid
    {
        private Map map;
        private bool[] canFlyOver;
        private int mapSizeX;
        private int mapSizeZ;

        // A* pathfinding data structures
        private static readonly int[] neighborOffsetsX = { 0, 1, 1, 1, 0, -1, -1, -1 };
        private static readonly int[] neighborOffsetsZ = { 1, 1, 0, -1, -1, -1, 0, 1 };
        private static readonly float[] neighborCosts = { 1f, 1.414f, 1f, 1.414f, 1f, 1.414f, 1f, 1.414f };

        public FlightPathGrid(Map map)
        {
            this.map = map;
            this.mapSizeX = map.Size.x;
            this.mapSizeZ = map.Size.z;
            this.canFlyOver = new bool[mapSizeX * mapSizeZ];
            RebuildGrid();
        }

        /// <summary>
        /// Rebuilds the flight passability grid. Call when map terrain changes significantly.
        /// </summary>
        public void RebuildGrid()
        {
            for (int z = 0; z < mapSizeZ; z++)
            {
                for (int x = 0; x < mapSizeX; x++)
                {
                    IntVec3 cell = new IntVec3(x, 0, z);
                    canFlyOver[CellToIndex(x, z)] = !IsFlightBlocked(cell);
                }
            }
        }

        /// <summary>
        /// Updates a single cell in the grid. Call when a building is placed/removed.
        /// </summary>
        public void UpdateCell(IntVec3 cell)
        {
            if (cell.InBounds(map))
            {
                canFlyOver[CellToIndex(cell.x, cell.z)] = !IsFlightBlocked(cell);
            }
        }

        /// <summary>
        /// Checks if a cell blocks flight (mountains and natural rock).
        /// </summary>
        private bool IsFlightBlocked(IntVec3 cell)
        {
            // Mountains = thick rock roof
            if (cell.Roofed(map) && map.roofGrid.RoofAt(cell) == RoofDefOf.RoofRockThick)
                return true;

            // Impassable natural rock (not smoothed)
            Building edifice = cell.GetEdifice(map);
            if (edifice != null && edifice.def.building != null &&
                edifice.def.building.isNaturalRock && !edifice.def.IsSmoothed)
                return true;

            return false;
        }

        /// <summary>
        /// Checks if a DART can fly over the given cell.
        /// </summary>
        public bool CanFlyOver(IntVec3 cell)
        {
            if (!cell.InBounds(map))
                return false;
            return canFlyOver[CellToIndex(cell.x, cell.z)];
        }

        /// <summary>
        /// Checks if a DART can fly over the given cell (coordinates version).
        /// </summary>
        public bool CanFlyOver(int x, int z)
        {
            if (x < 0 || x >= mapSizeX || z < 0 || z >= mapSizeZ)
                return false;
            return canFlyOver[CellToIndex(x, z)];
        }

        private int CellToIndex(int x, int z)
        {
            return z * mapSizeX + x;
        }

        private IntVec3 IndexToCell(int index)
        {
            return new IntVec3(index % mapSizeX, 0, index / mapSizeX);
        }

        /// <summary>
        /// Finds a flight path from start to destination using A* pathfinding.
        /// Returns null if no path exists.
        /// </summary>
        public List<IntVec3> FindPath(IntVec3 start, IntVec3 destination)
        {
            if (!start.InBounds(map) || !destination.InBounds(map))
                return null;

            if (!CanFlyOver(destination))
                return null;

            // If start is blocked, try to find nearby flyable cell
            if (!CanFlyOver(start))
            {
                start = FindNearestFlyableCell(start);
                if (!start.IsValid)
                    return null;
            }

            int startIndex = CellToIndex(start.x, start.z);
            int destIndex = CellToIndex(destination.x, destination.z);

            // Fast path: if very close, use direct line
            if (start.DistanceTo(destination) < 5f && HasDirectLine(start, destination))
            {
                return new List<IntVec3> { start, destination };
            }

            // A* implementation
            Dictionary<int, float> gScore = new Dictionary<int, float>();
            Dictionary<int, float> fScore = new Dictionary<int, float>();
            Dictionary<int, int> cameFrom = new Dictionary<int, int>();
            HashSet<int> closedSet = new HashSet<int>();

            // Priority queue using sorted list (index, fScore)
            SortedSet<(float score, int index, int tiebreaker)> openSet =
                new SortedSet<(float, int, int)>();
            HashSet<int> openSetIndices = new HashSet<int>();

            gScore[startIndex] = 0;
            fScore[startIndex] = Heuristic(start, destination);
            openSet.Add((fScore[startIndex], startIndex, 0));
            openSetIndices.Add(startIndex);

            int tiebreaker = 1;
            int iterations = 0;
            int maxIterations = mapSizeX * mapSizeZ; // Prevent infinite loops

            while (openSet.Count > 0 && iterations < maxIterations)
            {
                iterations++;

                var current = openSet.Min;
                openSet.Remove(current);
                int currentIndex = current.index;
                openSetIndices.Remove(currentIndex);

                if (currentIndex == destIndex)
                {
                    // Reconstruct path
                    return ReconstructPath(cameFrom, currentIndex);
                }

                closedSet.Add(currentIndex);
                IntVec3 currentCell = IndexToCell(currentIndex);

                // Check all 8 neighbors
                for (int i = 0; i < 8; i++)
                {
                    int nx = currentCell.x + neighborOffsetsX[i];
                    int nz = currentCell.z + neighborOffsetsZ[i];

                    if (nx < 0 || nx >= mapSizeX || nz < 0 || nz >= mapSizeZ)
                        continue;

                    int neighborIndex = CellToIndex(nx, nz);

                    if (closedSet.Contains(neighborIndex))
                        continue;

                    if (!canFlyOver[neighborIndex])
                        continue;

                    // For diagonal movement, check if we can actually move diagonally
                    // (both adjacent cardinal cells must be passable to avoid corner cutting)
                    if (i % 2 == 1) // Diagonal
                    {
                        int cardX1 = currentCell.x + neighborOffsetsX[(i + 7) % 8];
                        int cardZ1 = currentCell.z + neighborOffsetsZ[(i + 7) % 8];
                        int cardX2 = currentCell.x + neighborOffsetsX[(i + 1) % 8];
                        int cardZ2 = currentCell.z + neighborOffsetsZ[(i + 1) % 8];

                        if (!CanFlyOver(cardX1, cardZ1) || !CanFlyOver(cardX2, cardZ2))
                            continue;
                    }

                    float tentativeG = gScore[currentIndex] + neighborCosts[i];

                    if (!gScore.ContainsKey(neighborIndex) || tentativeG < gScore[neighborIndex])
                    {
                        cameFrom[neighborIndex] = currentIndex;
                        gScore[neighborIndex] = tentativeG;
                        float h = Heuristic(new IntVec3(nx, 0, nz), destination);
                        fScore[neighborIndex] = tentativeG + h;

                        if (!openSetIndices.Contains(neighborIndex))
                        {
                            openSet.Add((fScore[neighborIndex], neighborIndex, tiebreaker++));
                            openSetIndices.Add(neighborIndex);
                        }
                    }
                }
            }

            // No path found
            return null;
        }

        /// <summary>
        /// Reconstructs the path from A* result.
        /// </summary>
        private List<IntVec3> ReconstructPath(Dictionary<int, int> cameFrom, int currentIndex)
        {
            List<IntVec3> path = new List<IntVec3>();
            path.Add(IndexToCell(currentIndex));

            while (cameFrom.ContainsKey(currentIndex))
            {
                currentIndex = cameFrom[currentIndex];
                path.Add(IndexToCell(currentIndex));
            }

            path.Reverse();

            // Simplify path by removing unnecessary waypoints
            return SimplifyPath(path);
        }

        /// <summary>
        /// Simplifies a path by removing intermediate points when direct flight is possible.
        /// </summary>
        private List<IntVec3> SimplifyPath(List<IntVec3> path)
        {
            if (path.Count <= 2)
                return path;

            List<IntVec3> simplified = new List<IntVec3>();
            simplified.Add(path[0]);

            int i = 0;
            while (i < path.Count - 1)
            {
                // Find the furthest point we can reach directly
                int furthest = i + 1;
                for (int j = path.Count - 1; j > i + 1; j--)
                {
                    if (HasDirectLine(path[i], path[j]))
                    {
                        furthest = j;
                        break;
                    }
                }

                simplified.Add(path[furthest]);
                i = furthest;
            }

            return simplified;
        }

        /// <summary>
        /// Checks if there's a direct flyable line between two points (Bresenham's line).
        /// </summary>
        public bool HasDirectLine(IntVec3 from, IntVec3 to)
        {
            int x0 = from.x;
            int z0 = from.z;
            int x1 = to.x;
            int z1 = to.z;

            int dx = Math.Abs(x1 - x0);
            int dz = Math.Abs(z1 - z0);
            int sx = x0 < x1 ? 1 : -1;
            int sz = z0 < z1 ? 1 : -1;
            int err = dx - dz;

            while (true)
            {
                if (!CanFlyOver(x0, z0))
                    return false;

                if (x0 == x1 && z0 == z1)
                    break;

                int e2 = 2 * err;
                if (e2 > -dz)
                {
                    err -= dz;
                    x0 += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    z0 += sz;
                }
            }

            return true;
        }

        /// <summary>
        /// Heuristic for A* (octile distance).
        /// </summary>
        private float Heuristic(IntVec3 a, IntVec3 b)
        {
            int dx = Math.Abs(a.x - b.x);
            int dz = Math.Abs(a.z - b.z);
            // Octile distance: diagonal movement costs sqrt(2) ~= 1.414
            return Math.Max(dx, dz) + 0.414f * Math.Min(dx, dz);
        }

        /// <summary>
        /// Finds the nearest flyable cell to a given position.
        /// </summary>
        public IntVec3 FindNearestFlyableCell(IntVec3 from)
        {
            if (CanFlyOver(from))
                return from;

            // Spiral outward search
            for (int radius = 1; radius <= 10; radius++)
            {
                for (int x = -radius; x <= radius; x++)
                {
                    for (int z = -radius; z <= radius; z++)
                    {
                        if (Math.Abs(x) != radius && Math.Abs(z) != radius)
                            continue; // Only check perimeter

                        IntVec3 cell = new IntVec3(from.x + x, 0, from.z + z);
                        if (cell.InBounds(map) && CanFlyOver(cell))
                            return cell;
                    }
                }
            }

            return IntVec3.Invalid;
        }

        /// <summary>
        /// Gets a random flyable cell within range of a position.
        /// Used for loitering behavior.
        /// </summary>
        public IntVec3 GetRandomFlyableCellNear(IntVec3 center, float radius)
        {
            for (int attempts = 0; attempts < 20; attempts++)
            {
                float angle = Rand.Range(0f, 360f) * Mathf.Deg2Rad;
                float dist = Rand.Range(radius * 0.5f, radius);
                int x = center.x + Mathf.RoundToInt(Mathf.Cos(angle) * dist);
                int z = center.z + Mathf.RoundToInt(Mathf.Sin(angle) * dist);
                IntVec3 cell = new IntVec3(x, 0, z);

                if (cell.InBounds(map) && CanFlyOver(cell))
                    return cell;
            }

            return center; // Fallback to center
        }
    }
}
