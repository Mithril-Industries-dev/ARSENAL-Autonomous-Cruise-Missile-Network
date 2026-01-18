using System.Collections.Generic;
using System.Linq;
using Verse;

namespace Arsenal
{
    public static class ArsenalNetworkManager
    {
        private static List<Building_Arsenal> arsenals = new List<Building_Arsenal>();
        private static List<Building_Hub> hubs = new List<Building_Hub>();
        private static List<Building_Hop> hops = new List<Building_Hop>();

        // LATTICE system components
        private static List<Building_Lattice> lattices = new List<Building_Lattice>();
        private static List<Building_Quiver> quivers = new List<Building_Quiver>();

        public static void RegisterArsenal(Building_Arsenal arsenal)
        {
            if (!arsenals.Contains(arsenal))
                arsenals.Add(arsenal);
        }

        public static void DeregisterArsenal(Building_Arsenal arsenal)
        {
            arsenals.Remove(arsenal);
        }

        public static void RegisterHub(Building_Hub hub)
        {
            if (!hubs.Contains(hub))
                hubs.Add(hub);
        }

        public static void DeregisterHub(Building_Hub hub)
        {
            hubs.Remove(hub);
        }

        public static void RegisterHop(Building_Hop hop)
        {
            if (!hops.Contains(hop))
                hops.Add(hop);
        }

        public static void DeregisterHop(Building_Hop hop)
        {
            hops.Remove(hop);
        }

        // LATTICE registration
        public static void RegisterLattice(Building_Lattice lattice)
        {
            if (!lattices.Contains(lattice))
                lattices.Add(lattice);
        }

        public static void DeregisterLattice(Building_Lattice lattice)
        {
            lattices.Remove(lattice);
        }

        // QUIVER registration
        public static void RegisterQuiver(Building_Quiver quiver)
        {
            if (!quivers.Contains(quiver))
                quivers.Add(quiver);
        }

        public static void DeregisterQuiver(Building_Quiver quiver)
        {
            quivers.Remove(quiver);
        }

        public static List<Building_Arsenal> GetAllArsenals()
        {
            arsenals.RemoveAll(a => a == null || a.Destroyed);
            return arsenals.ToList();
        }

        public static List<Building_Hub> GetAllHubs()
        {
            hubs.RemoveAll(h => h == null || h.Destroyed);
            return hubs.ToList();
        }

        public static List<Building_Hop> GetAllHops()
        {
            hops.RemoveAll(h => h == null || h.Destroyed);
            return hops.ToList();
        }

        public static List<Building_Lattice> GetAllLattices()
        {
            lattices.RemoveAll(l => l == null || l.Destroyed);
            return lattices.ToList();
        }

        public static List<Building_Quiver> GetAllQuivers()
        {
            quivers.RemoveAll(q => q == null || q.Destroyed);
            return quivers.ToList();
        }

        public static Building_Hub GetHubAtTile(int tile)
        {
            return GetAllHubs().FirstOrDefault(h => h.Map != null && h.Map.Tile == tile);
        }

        public static Building_Hop GetHopAtTile(int tile)
        {
            return GetAllHops().FirstOrDefault(h => h.Map != null && h.Map.Tile == tile);
        }

        // NEW: Get an available (not refueling) HOP at a specific tile
        public static Building_Hop GetAvailableHopAtTile(int tile)
        {
            return GetAllHops().FirstOrDefault(h =>
                h.Map != null &&
                h.Map.Tile == tile &&
                h.CanAcceptMissile());
        }

        // NEW: Get all HOPs at a specific tile
        public static List<Building_Hop> GetAllHopsAtTile(int tile)
        {
            return GetAllHops().Where(h => h.Map != null && h.Map.Tile == tile).ToList();
        }

        // Get LATTICE on a specific map (only one allowed per map)
        public static Building_Lattice GetLatticeOnMap(Map map)
        {
            if (map == null) return null;
            return GetAllLattices().FirstOrDefault(l => l.Map == map);
        }

        // Get all QUIVERs on a specific map
        public static List<Building_Quiver> GetQuiversOnMap(Map map)
        {
            if (map == null) return new List<Building_Quiver>();
            return GetAllQuivers().Where(q => q.Map == map).ToList();
        }

        // Get ARSENAL on a specific map (for DART manufacturing)
        public static Building_Arsenal GetArsenalOnMap(Map map)
        {
            if (map == null) return null;
            return GetAllArsenals().FirstOrDefault(a => a.Map == map);
        }

        public static void Reset()
        {
            arsenals.Clear();
            hubs.Clear();
            hops.Clear();
            lattices.Clear();
            quivers.Clear();
        }
    }

    public class GameComponent_ArsenalNetwork : GameComponent
    {
        public GameComponent_ArsenalNetwork(Game game) { }

        public override void StartedNewGame()
        {
            base.StartedNewGame();
            ArsenalNetworkManager.Reset();
        }

        public override void LoadedGame()
        {
            base.LoadedGame();
        }
    }
}